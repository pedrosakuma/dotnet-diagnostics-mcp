using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator.Investigations;

public sealed class MemoryInvestigationSessionBinderTests
{
    [Fact]
    public void TryGetHandleId_ReturnsNull_WhenNoBinding()
    {
        var binder = new MemoryInvestigationSessionBinder();
        binder.TryGetHandleId("session-1").Should().BeNull();
    }

    [Fact]
    public void TryGetHandleId_ReturnsNull_ForNullOrEmptySessionId()
    {
        var binder = new MemoryInvestigationSessionBinder();
        binder.TryGetHandleId(null).Should().BeNull();
        binder.TryGetHandleId(string.Empty).Should().BeNull();
    }

    [Fact]
    public void Bind_ThenGet_ReturnsTheHandle()
    {
        var binder = new MemoryInvestigationSessionBinder();
        binder.Bind("session-1", "handle-A");
        binder.TryGetHandleId("session-1").Should().Be("handle-A");
    }

    [Fact]
    public void Bind_Replaces_PreviousHandle()
    {
        var binder = new MemoryInvestigationSessionBinder();
        binder.Bind("session-1", "handle-A");
        binder.Bind("session-1", "handle-B");
        binder.TryGetHandleId("session-1").Should().Be("handle-B");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Bind_Throws_OnEmptySessionId(string? sessionId)
    {
        var binder = new MemoryInvestigationSessionBinder();
        FluentActions.Invoking(() => binder.Bind(sessionId!, "handle"))
            .Should().Throw<System.ArgumentException>().WithParameterName("sessionId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Bind_Throws_OnEmptyHandleId(string? handleId)
    {
        var binder = new MemoryInvestigationSessionBinder();
        FluentActions.Invoking(() => binder.Bind("session-1", handleId!))
            .Should().Throw<System.ArgumentException>().WithParameterName("handleId");
    }

    [Fact]
    public void Unbind_ReturnsHandle_AndRemovesIt()
    {
        var binder = new MemoryInvestigationSessionBinder();
        binder.Bind("session-1", "handle-A");
        binder.Unbind("session-1").Should().Be("handle-A");
        binder.TryGetHandleId("session-1").Should().BeNull();
    }

    [Fact]
    public void Unbind_ReturnsNull_WhenNothingBound()
    {
        var binder = new MemoryInvestigationSessionBinder();
        binder.Unbind("session-1").Should().BeNull();
        binder.Unbind(null).Should().BeNull();
        binder.Unbind(string.Empty).Should().BeNull();
    }

    [Fact]
    public void UnbindAllForHandle_RemovesEverySessionPointingAtHandle()
    {
        var binder = new MemoryInvestigationSessionBinder();
        binder.Bind("session-1", "handle-A");
        binder.Bind("session-2", "handle-A");
        binder.Bind("session-3", "handle-B");

        var removed = binder.UnbindAllForHandle("handle-A");

        removed.Should().BeEquivalentTo(new[] { "session-1", "session-2" });
        binder.TryGetHandleId("session-1").Should().BeNull();
        binder.TryGetHandleId("session-2").Should().BeNull();
        binder.TryGetHandleId("session-3").Should().Be("handle-B");
    }

    [Fact]
    public void UnbindAllForHandle_ReturnsEmpty_WhenNoMatch()
    {
        var binder = new MemoryInvestigationSessionBinder();
        binder.Bind("session-1", "handle-A");
        binder.UnbindAllForHandle("handle-missing").Should().BeEmpty();
        binder.UnbindAllForHandle(string.Empty).Should().BeEmpty();
        binder.TryGetHandleId("session-1").Should().Be("handle-A");
    }

    [Fact]
    public void Snapshot_ReturnsAllBindings()
    {
        var binder = new MemoryInvestigationSessionBinder();
        binder.Bind("session-1", "handle-A");
        binder.Bind("session-2", "handle-B");

        binder.Snapshot().Should().BeEquivalentTo(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, string>("session-1", "handle-A"),
            new System.Collections.Generic.KeyValuePair<string, string>("session-2", "handle-B"),
        });
    }

    [Fact]
    public void Snapshot_IsCopy_NotLiveView()
    {
        var binder = new MemoryInvestigationSessionBinder();
        binder.Bind("session-1", "handle-A");
        var snap = binder.Snapshot();
        binder.Bind("session-2", "handle-B");
        snap.Should().HaveCount(1);
    }

    [Fact]
    public void Bindings_AreThreadSafe_UnderConcurrentMutation()
    {
        var binder = new MemoryInvestigationSessionBinder();
        var tasks = new System.Threading.Tasks.Task[8];
        for (int t = 0; t < tasks.Length; t++)
        {
            int worker = t;
            tasks[t] = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    var sid = $"s-{worker}-{i % 50}";
                    binder.Bind(sid, $"h-{worker}");
                    _ = binder.TryGetHandleId(sid);
                    if (i % 7 == 0) binder.Unbind(sid);
                    if (i % 25 == 0) binder.UnbindAllForHandle($"h-{worker}");
                }
            });
        }
        System.Threading.Tasks.Task.WaitAll(tasks);
        // No assertion beyond "no exception under concurrent mutation".
    }
}
