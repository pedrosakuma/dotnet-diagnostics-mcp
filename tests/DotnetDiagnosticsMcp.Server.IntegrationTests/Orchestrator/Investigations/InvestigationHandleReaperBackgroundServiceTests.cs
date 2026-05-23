using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Hosting;
using DotnetDiagnosticsMcp.Server.Observability;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator.Investigations;

/// <summary>
/// Unit tests for <see cref="InvestigationHandleReaperBackgroundService.ReapExpiredAsync"/>.
/// </summary>
public sealed class InvestigationHandleReaperBackgroundServiceTests
{
    private static InvestigationHandle Handle(string id, InvestigationState state, DateTimeOffset expiresAt) => new(
        HandleId: id,
        Namespace: "ns",
        PodName: "pod",
        TargetContainerName: "api",
        EphemeralContainerName: "diag",
        PodLocalBearerToken: "secret",
        State: state,
        AttachedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
        ExpiresAt: expiresAt);

    [Fact]
    public async Task ReapExpiredAsync_TransitionsActiveHandlesPastTtl_ToExpired()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        fx.Store.Add(Handle("expired", InvestigationState.Active, now.AddSeconds(-1)));
        fx.Store.Add(Handle("fresh", InvestigationState.Active, now.AddMinutes(5)));
        fx.Store.Add(Handle("stuck-attach", InvestigationState.Attaching, now.AddSeconds(-30)));
        fx.Store.Add(Handle("already-closed", InvestigationState.Closed, now.AddSeconds(-1)));

        var reaped = await fx.Reaper.ReapExpiredAsync(now);

        reaped.Should().Be(2);
        fx.Store.GetById("expired")!.State.Should().Be(InvestigationState.Expired);
        fx.Store.GetById("stuck-attach")!.State.Should().Be(InvestigationState.Expired);
        fx.Store.GetById("fresh")!.State.Should().Be(InvestigationState.Active);
        fx.Store.GetById("already-closed")!.State.Should().Be(InvestigationState.Closed);

        fx.Proxy.DisposeCalls.Should().BeEquivalentTo(new[] { "expired", "stuck-attach" });
        fx.PortForward.CloseCalls.Should().BeEquivalentTo(new[] { "expired", "stuck-attach" });
    }

    [Fact]
    public async Task ReapExpiredAsync_RecordsTtlReasonOnExpiry()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var ttl = now.AddSeconds(-5);
        fx.Store.Add(Handle("h", InvestigationState.Active, ttl));

        await fx.Reaper.ReapExpiredAsync(now);

        var after = fx.Store.GetById("h")!;
        after.State.Should().Be(InvestigationState.Expired);
        after.FailureReason.Should().NotBeNullOrEmpty();
        after.FailureReason.Should().Contain("TTL expired");
    }

    [Fact]
    public async Task ReapExpiredAsync_EmptyStore_IsNoOp()
    {
        var fx = new Fixture();
        var reaped = await fx.Reaper.ReapExpiredAsync(DateTimeOffset.UtcNow);
        reaped.Should().Be(0);
    }

    private sealed class Fixture
    {
        public MemoryInvestigationStore Store { get; } = new();
        public CountingProxy Proxy { get; } = new();
        public CountingPortForward PortForward { get; } = new();
        public MemoryInvestigationSessionBinder Binder { get; } = new();
        public InvestigationHandleReaperBackgroundService Reaper { get; }

        public Fixture()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            var provider = services.BuildServiceProvider();
            var observability = new OrchestratorObservability(
                provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
                Store,
                new AuditLogWriter(TextWriter.Null));
            var closer = new InvestigationCloser(Store, Proxy, PortForward, Binder);
            Reaper = new InvestigationHandleReaperBackgroundService(Store, closer, observability);
        }
    }

    private sealed class CountingProxy : IInvestigationProxyClient
    {
        public List<string> DisposeCalls { get; } = new();
        public Task<CallToolResult> CallToolAsync(InvestigationHandle handle, CallToolRequestParams request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task DisposeForHandleAsync(string handleId) { DisposeCalls.Add(handleId); return Task.CompletedTask; }
    }

    private sealed class CountingPortForward : IPortForwardManager
    {
        public List<string> CloseCalls { get; } = new();
        public Task<System.Net.Http.HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task CloseAsync(string handleId) { CloseCalls.Add(handleId); return Task.CompletedTask; }
    }
}
