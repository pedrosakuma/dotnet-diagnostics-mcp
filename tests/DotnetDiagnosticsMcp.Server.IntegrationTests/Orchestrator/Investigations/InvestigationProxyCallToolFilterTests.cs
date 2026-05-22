using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator;

/// <summary>
/// Unit tests for <see cref="InvestigationProxyCallToolFilter.InvokeAsync"/>. The filter
/// is exercised through its core method so tests don't have to construct an McpServer
/// (its abstract surface is non-trivial; the surrounding <see cref="InvestigationProxyEndpointTests"/>
/// covers the wired-up DI path end-to-end).
/// </summary>
public sealed class InvestigationProxyCallToolFilterTests
{
    private static readonly InvestigationHandle ActiveHandle = new(
        HandleId: "inv-1",
        Namespace: "ns",
        PodName: "pod-a",
        TargetContainerName: "api",
        EphemeralContainerName: "diag-1",
        PodLocalBearerToken: "pod-bearer",
        State: InvestigationState.Active,
        AttachedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30));

    private static readonly InvestigationHandle FailedHandle = ActiveHandle with { HandleId = "inv-failed", State = InvestigationState.Failed };

    [Fact]
    public async Task PassesThrough_WhenSessionIdIsNullOrEmpty()
    {
        var fx = new Fixture();
        var result = await fx.Invoke(Params("snapshot_counters"), sessionId: null);

        result.IsError.Should().BeNull();
        fx.LocalInvocations.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PassesThrough_WhenSessionHasNoBinding()
    {
        var fx = new Fixture();
        var result = await fx.Invoke(Params("snapshot_counters"), sessionId: "session-unbound");

        result.IsError.Should().BeNull();
        fx.LocalInvocations.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("list_pods")]
    [InlineData("attach_to_pod")]
    [InlineData("detach_from_pod")]
    [InlineData("list_active_investigations")]
    public async Task PassesThrough_WhenToolIsOrchestratorBypassed(string toolName)
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-1", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(Params(toolName), sessionId: "session-1");

        result.IsError.Should().BeNull();
        fx.LocalInvocations.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("4242")]
    [InlineData("\"4242\"")]
    public async Task PassesThrough_WhenArgumentsCarryExplicitProcessId(string processIdJson)
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-pid", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["processId"] = JsonDocument.Parse(processIdJson).RootElement,
        };
        var result = await fx.Invoke(Params("snapshot_counters", args), sessionId: "session-pid");

        result.IsError.Should().BeNull();
        fx.LocalInvocations.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PassesThrough_WhenBoundHandleIsNotActive()
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-fail", FailedHandle.HandleId);
        fx.Store.Add(FailedHandle);

        var result = await fx.Invoke(Params("snapshot_counters"), sessionId: "session-fail");

        result.IsError.Should().BeNull();
        fx.LocalInvocations.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PassesThrough_WhenBoundHandleIsMissingFromStore()
    {
        var fx = new Fixture();
        // Binder knows about a handle the store evicted (race during TTL reaping).
        fx.Binder.Bind("session-orphan", "inv-vanished");

        var result = await fx.Invoke(Params("snapshot_counters"), sessionId: "session-orphan");

        result.IsError.Should().BeNull();
        fx.LocalInvocations.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Forwards_WhenSessionBoundToActiveHandleAndArgsImplicit()
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-ok", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var upstream = new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = "upstream-ok" } },
        };
        fx.ProxyClient.Next = (_, _, _) => Task.FromResult(upstream);

        var p = Params("snapshot_counters");
        var result = await fx.Invoke(p, sessionId: "session-ok");

        result.Should().BeSameAs(upstream);
        fx.ProxyClient.CallCount.Should().Be(1);
        fx.ProxyClient.LastHandle.Should().BeSameAs(ActiveHandle);
        fx.ProxyClient.LastRequest.Should().BeSameAs(p);
        fx.LocalInvocations.Should().Be(0);
    }

    [Fact]
    public async Task ForwardingFailure_SurfacesStructuredError_AndDoesNotFallThrough()
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-err", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var thrown = new InvalidOperationException("upstream MCP exploded");
        fx.ProxyClient.Next = (_, _, _) => throw thrown;

        var result = await fx.Invoke(Params("snapshot_counters"), sessionId: "session-err");

        result.IsError.Should().Be(true);
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().Contain("snapshot_counters failed: proxy forwarding to investigation inv-1");
        text.Should().Contain(nameof(InvalidOperationException));
        text.Should().Contain("upstream MCP exploded");
        fx.LocalInvocations.Should().Be(0);
    }

    [Fact]
    public async Task ForwardingFailure_RethrowsMcpProtocolException()
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-proto", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        fx.ProxyClient.Next = (_, _, _) => throw new McpProtocolException("bad rpc");

        var act = async () => await fx.Invoke(Params("snapshot_counters"), sessionId: "session-proto");

        await act.Should().ThrowAsync<McpProtocolException>().WithMessage("bad rpc");
        fx.LocalInvocations.Should().Be(0);
    }

    [Fact]
    public async Task ForwardingFailure_RethrowsOperationCanceled_WhenCallerCancelled()
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-cancel", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        fx.ProxyClient.Next = (_, _, ct) => Task.FromException<CallToolResult>(new OperationCanceledException(ct));

        var act = async () => await fx.Invoke(Params("snapshot_counters"), sessionId: "session-cancel", token: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fx.LocalInvocations.Should().Be(0);
    }

    [Theory]
    [InlineData("4242", true)]
    [InlineData("\"4242\"", true)]
    [InlineData("\" \"", false)]
    [InlineData("\"\"", false)]
    [InlineData("null", false)]
    public void HasExplicitProcessId_RecognisesValueShapes(string json, bool expected)
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["processId"] = JsonDocument.Parse(json).RootElement,
        };
        InvestigationProxyCallToolFilter.HasExplicitProcessId(args).Should().Be(expected);
    }

    [Fact]
    public void HasExplicitProcessId_FalseOnMissingKey()
    {
        InvestigationProxyCallToolFilter.HasExplicitProcessId(null).Should().BeFalse();
        InvestigationProxyCallToolFilter.HasExplicitProcessId(new Dictionary<string, JsonElement>()).Should().BeFalse();
    }

    private static CallToolRequestParams Params(string toolName, IDictionary<string, JsonElement>? args = null)
        => new() { Name = toolName, Arguments = args };

    private sealed class Fixture
    {
        public InMemorySessionBinder Binder { get; } = new();
        public InMemoryInvestigationStore Store { get; } = new();
        public FakeProxyClient ProxyClient { get; } = new();
        public int LocalInvocations;

        public ValueTask<CallToolResult> Invoke(
            CallToolRequestParams? request, string? sessionId, CancellationToken token = default)
        {
            return InvestigationProxyCallToolFilter.InvokeAsync(
                request,
                sessionId,
                next: (_, _) =>
                {
                    Interlocked.Increment(ref LocalInvocations);
                    return ValueTask.FromResult(new CallToolResult());
                },
                Binder,
                Store,
                ProxyClient,
                loggerAccessor: () => null,
                token);
        }
    }

    private sealed class InMemorySessionBinder : IInvestigationSessionBinder
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

        public string? TryGetHandleId(string? sessionId)
            => sessionId is { Length: > 0 } && _map.TryGetValue(sessionId, out var h) ? h : null;

        public void Bind(string sessionId, string handleId) => _map[sessionId] = handleId;

        public string? Unbind(string? sessionId)
        {
            if (sessionId is null || !_map.TryGetValue(sessionId, out var h)) return null;
            _map.Remove(sessionId);
            return h;
        }

        public IReadOnlyCollection<string> UnbindAllForHandle(string handleId)
        {
            var removed = _map.Where(kv => kv.Value == handleId).Select(kv => kv.Key).ToList();
            foreach (var k in removed) _map.Remove(k);
            return removed;
        }

        public IReadOnlyCollection<KeyValuePair<string, string>> Snapshot() => _map.ToArray();
    }

    private sealed class InMemoryInvestigationStore : IInvestigationStore
    {
        private readonly Dictionary<string, InvestigationHandle> _byId = new(StringComparer.Ordinal);

        public void Add(InvestigationHandle handle) => _byId[handle.HandleId] = handle;

        public bool TryReserveTarget(InvestigationHandle newHandle, bool allowReuse, out InvestigationHandle? existing)
        {
            existing = null;
            _byId[newHandle.HandleId] = newHandle;
            return true;
        }

        public void Update(InvestigationHandle handle) => _byId[handle.HandleId] = handle;
        public InvestigationHandle? GetById(string handleId) => _byId.TryGetValue(handleId, out var h) ? h : null;
        public InvestigationTerminalTransition TryTransitionToTerminal(
            string handleId,
            InvestigationState targetState,
            string? failureReason,
            out InvestigationState? previousState)
        {
            previousState = null;
            if (!_byId.TryGetValue(handleId, out var current)) return InvestigationTerminalTransition.NotFound;
            previousState = current.State;
            if (current.State is InvestigationState.Closed or InvestigationState.Expired or InvestigationState.Failed)
                return InvestigationTerminalTransition.AlreadyTerminal;
            _byId[handleId] = current with { State = targetState, FailureReason = targetState == InvestigationState.Closed ? current.FailureReason : failureReason ?? current.FailureReason };
            return InvestigationTerminalTransition.Transitioned;
        }
        public InvestigationHandle? FindReusableTarget(string podNamespace, string podName, string containerName) => null;
        public IReadOnlyCollection<InvestigationHandle> Snapshot() => _byId.Values.ToArray();
    }

    private sealed class FakeProxyClient : IInvestigationProxyClient
    {
        public int CallCount;
        public InvestigationHandle? LastHandle;
        public CallToolRequestParams? LastRequest;
        public Func<InvestigationHandle, CallToolRequestParams, CancellationToken, Task<CallToolResult>> Next
            = (_, _, _) => Task.FromResult(new CallToolResult());

        public Task<CallToolResult> CallToolAsync(InvestigationHandle handle, CallToolRequestParams request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            LastHandle = handle;
            LastRequest = request;
            return Next(handle, request, cancellationToken);
        }

        public int DisposeCallCount;
        public string? LastDisposedHandleId;

        public Task DisposeForHandleAsync(string handleId)
        {
            Interlocked.Increment(ref DisposeCallCount);
            LastDisposedHandleId = handleId;
            return Task.CompletedTask;
        }
    }
}
