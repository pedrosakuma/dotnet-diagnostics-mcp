using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Observability;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using DotnetDiagnosticsMcp.Server.Security;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator;

/// <summary>
/// Unit tests for the P4 orchestrator MCP tools (<c>detach_from_pod</c>,
/// <c>list_active_investigations</c>). The McpServer parameter is only used to read
/// SessionId via reflection; tests that don't exercise the session-fallback branch
/// pass <c>null!</c> through.
/// </summary>
public sealed class OrchestratorToolsP4Tests
{
    private static InvestigationHandle Active(string id = "h-1", DateTimeOffset? attachedAt = null) => new(
        HandleId: id,
        Namespace: "ns",
        PodName: "pod",
        TargetContainerName: "api",
        EphemeralContainerName: "diag",
        PodLocalBearerToken: "secret",
        State: InvestigationState.Active,
        AttachedAt: attachedAt ?? DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30));

    // ---- detach_from_pod -----------------------------------------------------------------

    [Fact]
    public async Task DetachFromPod_KnownActiveHandle_TransitionsToClosedAndUnbindsSessions()
    {
        var fx = new Fixture();
        var h = Active();
        fx.Store.Add(h);
        fx.Binder.Bind("sess-a", h.HandleId);

        var result = await OrchestratorTools.DetachFromPod(
            fx.Closer, fx.Binder, fx.Store, fx.Options,
            TestPrincipalAccessors.Root, fx.Observability, server: null!, handleId: h.HandleId);

        result.IsError.Should().BeFalse();
        result.Data.Should().NotBeNull();
        result.Data!.Found.Should().BeTrue();
        result.Data.AlreadyTerminal.Should().BeFalse();
        result.Data.NewState.Should().Be(InvestigationState.Closed);
        result.Data.UnboundSessionIds.Should().Contain("sess-a");
        fx.Store.GetById(h.HandleId)!.State.Should().Be(InvestigationState.Closed);
    }

    [Fact]
    public async Task DetachFromPod_UnknownHandle_ReturnsOkNoOp()
    {
        var fx = new Fixture();
        var result = await OrchestratorTools.DetachFromPod(
            fx.Closer, fx.Binder, fx.Store, fx.Options,
            TestPrincipalAccessors.Root, fx.Observability, server: null!, handleId: "missing");

        result.IsError.Should().BeFalse();
        result.Data!.Found.Should().BeFalse();
        result.Summary.Should().Contain("unknown");
    }

    [Fact]
    public async Task DetachFromPod_NoHandleId_NoSessionBinding_ReturnsOkNoOpWithGuidance()
    {
        var fx = new Fixture();
        var result = await OrchestratorTools.DetachFromPod(
            fx.Closer, fx.Binder, fx.Store, fx.Options,
            TestPrincipalAccessors.Root, fx.Observability, server: null!, handleId: null);

        result.IsError.Should().BeFalse();
        result.Data!.HandleId.Should().BeEmpty();
        result.Data.Found.Should().BeFalse();
        result.Hints.Should().NotBeEmpty();
        result.Hints!.Any(h => string.Equals(h.NextTool, "list_orchestrator", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public async Task DetachFromPod_AlreadyClosed_ReportsAlreadyTerminal()
    {
        var fx = new Fixture();
        var h = Active() with { State = InvestigationState.Closed };
        fx.Store.Add(h);

        var result = await OrchestratorTools.DetachFromPod(
            fx.Closer, fx.Binder, fx.Store, fx.Options,
            TestPrincipalAccessors.Root, fx.Observability, server: null!, handleId: h.HandleId);

        result.IsError.Should().BeFalse();
        result.Data!.AlreadyTerminal.Should().BeTrue();
        result.Data.PreviousState.Should().Be(InvestigationState.Closed);
    }

    [Fact]
    public async Task DetachFromPod_OwnerMismatch_ReturnsPermissionDenied()
    {
        // B3 review (issue #164): a handle owned by another MCP session must not be
        // closable by an unrelated caller — otherwise any authenticated peer can DoS
        // another session's investigation just by knowing the handle id.
        var fx = new Fixture();
        var h = Active() with { OwnerSessionId = "sess-other" };
        fx.Store.Add(h);

        var result = await OrchestratorTools.DetachFromPod(
            fx.Closer, fx.Binder, fx.Store, fx.Options,
            TestPrincipalAccessors.Root, fx.Observability, server: null!, handleId: h.HandleId);

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be("PermissionDenied");
        fx.Store.GetById(h.HandleId)!.State.Should().Be(InvestigationState.Active);
    }

    [Fact]
    public async Task DetachFromPod_OwnerMismatch_AdminOverride_Allows()
    {
        var fx = new Fixture { Options = { AllowCrossSessionAdmin = true } };
        var h = Active() with { OwnerSessionId = "sess-other" };
        fx.Store.Add(h);

        var result = await OrchestratorTools.DetachFromPod(
            fx.Closer, fx.Binder, fx.Store, fx.Options,
            TestPrincipalAccessors.Root, fx.Observability, server: null!, handleId: h.HandleId);

        result.IsError.Should().BeFalse();
        fx.Store.GetById(h.HandleId)!.State.Should().Be(InvestigationState.Closed);
    }

    [Fact]
    public async Task DetachFromPod_OwnerMismatch_OrchestratorAdminScopeOverride_Allows()
    {
        // B5.3 (issue #184): the per-bearer 'orchestrator-admin' scope is the
        // scope-first replacement for AllowCrossSessionAdmin. With the flag OFF
        // and only the scope granted, the owner-mismatch close must still succeed.
        var fx = new Fixture();
        var h = Active() with { OwnerSessionId = "sess-other" };
        fx.Store.Add(h);

        var result = await OrchestratorTools.DetachFromPod(
            fx.Closer, fx.Binder, fx.Store, fx.Options,
            TestPrincipalAccessors.WithScopes("orchestrator-attach", "orchestrator-admin"),
            fx.Observability,
            server: null!, handleId: h.HandleId);

        result.IsError.Should().BeFalse();
        fx.Store.GetById(h.HandleId)!.State.Should().Be(InvestigationState.Closed);
    }

    // ---- list_active_investigations ------------------------------------------------------

    [Fact]
    public async Task ListActiveInvestigations_DefaultExcludesTerminalStates()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        fx.Store.Add(Active("active-1", attachedAt: now.AddSeconds(-10)));
        fx.Store.Add(Active("active-2", attachedAt: now.AddSeconds(-5)) with { State = InvestigationState.Attaching });
        fx.Store.Add(Active("closed-1") with { State = InvestigationState.Closed });
        fx.Store.Add(Active("expired-1") with { State = InvestigationState.Expired });
        fx.Store.Add(Active("failed-1") with { State = InvestigationState.Failed });

        var result = await OrchestratorTools.ListActiveInvestigations(fx.Store, fx.Options, TestPrincipalAccessors.Root);

        result.IsError.Should().BeFalse();
        result.Data.Should().NotBeNull();
        result.Data!.TotalKnown.Should().Be(5);
        result.Data.ActiveCount.Should().Be(1);
        result.Data.AttachingCount.Should().Be(1);
        result.Data.ClosedCount.Should().Be(1);
        result.Data.ExpiredCount.Should().Be(1);
        result.Data.FailedCount.Should().Be(1);
        result.Data.Items.Should().HaveCount(2);
        result.Data.Items.Select(i => i.HandleId).Should().BeEquivalentTo(new[] { "active-1", "active-2" });
    }

    [Fact]
    public async Task ListActiveInvestigations_IncludeTerminal_ReturnsEverything()
    {
        var fx = new Fixture();
        fx.Store.Add(Active("active-1"));
        fx.Store.Add(Active("closed-1") with { State = InvestigationState.Closed });

        var result = await OrchestratorTools.ListActiveInvestigations(fx.Store, fx.Options, TestPrincipalAccessors.Root, includeTerminal: true);

        result.IsError.Should().BeFalse();
        result.Data!.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListActiveInvestigations_ProjectsProxyUrl_OnlyForActiveHandles()
    {
        var fx = new Fixture();
        fx.Store.Add(Active("active-1"));
        fx.Store.Add(Active("attaching-1") with { State = InvestigationState.Attaching });

        var result = await OrchestratorTools.ListActiveInvestigations(fx.Store, fx.Options, TestPrincipalAccessors.Root);

        var activeItem = result.Data!.Items.Single(i => i.HandleId == "active-1");
        var attachingItem = result.Data.Items.Single(i => i.HandleId == "attaching-1");
        activeItem.ProxyBaseUrl.Should().Be(fx.Options.ProxyBasePath.TrimEnd('/') + "/active-1");
        attachingItem.ProxyBaseUrl.Should().BeNull();
    }

    [Fact]
    public async Task ListActiveInvestigations_NeverLeaksBearerToken()
    {
        var fx = new Fixture();
        fx.Store.Add(Active("h"));

        var result = await OrchestratorTools.ListActiveInvestigations(fx.Store, fx.Options, TestPrincipalAccessors.Root, includeTerminal: true);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.Should().NotContain("secret"); // PodLocalBearerToken value
        // AttachSession record has no bearer-token field, so this is structural — guard anyway.
        json.Should().NotContain("PodLocalBearerToken");
    }

    [Fact]
    public async Task ListActiveInvestigations_OrdersByAttachedAtDescending()
    {
        var fx = new Fixture();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        fx.Store.Add(Active("oldest", attachedAt: t0));
        fx.Store.Add(Active("middle", attachedAt: t0.AddMinutes(10)));
        fx.Store.Add(Active("newest", attachedAt: t0.AddMinutes(20)));

        var result = await OrchestratorTools.ListActiveInvestigations(fx.Store, fx.Options, TestPrincipalAccessors.Root);

        result.Data!.Items.Select(i => i.HandleId).Should().Equal("newest", "middle", "oldest");
    }

    [Fact]
    public async Task ListActiveInvestigations_CrossSession_HidesOtherSessionsHandles()
    {
        // B3 review (issue #164 Med 2): counts and items must be computed over the
        // visible-only set, not the global store, or the size of another session's
        // investigation surface leaks via TotalKnown / *Count.
        var fx = new Fixture();
        fx.Store.Add(Active("a1") with { OwnerSessionId = "sess-other" });
        fx.Store.Add(Active("a2") with { OwnerSessionId = "sess-other", State = InvestigationState.Attaching });
        fx.Store.Add(Active("c1") with { OwnerSessionId = "sess-other", State = InvestigationState.Closed });

        var result = await OrchestratorTools.ListActiveInvestigations(fx.Store, fx.Options, TestPrincipalAccessors.Root, server: null!, includeTerminal: true);

        result.IsError.Should().BeFalse();
        result.Data!.TotalKnown.Should().Be(0);
        result.Data.ActiveCount.Should().Be(0);
        result.Data.AttachingCount.Should().Be(0);
        result.Data.ClosedCount.Should().Be(0);
        result.Data.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ListActiveInvestigations_AdminOverride_SeesEveryHandle()
    {
        var fx = new Fixture { Options = { AllowCrossSessionAdmin = true } };
        fx.Store.Add(Active("a1") with { OwnerSessionId = "sess-other" });
        fx.Store.Add(Active("a2") with { OwnerSessionId = "sess-other", State = InvestigationState.Closed });

        var result = await OrchestratorTools.ListActiveInvestigations(fx.Store, fx.Options, TestPrincipalAccessors.Root, server: null!, includeTerminal: true, includeAllSessions: true);

        result.IsError.Should().BeFalse();
        result.Data!.TotalKnown.Should().Be(2);
        result.Data.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListActiveInvestigations_OrchestratorAdminScopeOverride_SeesEveryHandle()
    {
        // B5.3 (issue #184): when the principal carries the 'orchestrator-admin'
        // modifier scope, includeAllSessions=true returns cross-session handles
        // even with AllowCrossSessionAdmin=false.
        var fx = new Fixture();
        fx.Store.Add(Active("a1") with { OwnerSessionId = "sess-other" });
        fx.Store.Add(Active("a2") with { OwnerSessionId = "sess-other", State = InvestigationState.Closed });

        var result = await OrchestratorTools.ListActiveInvestigations(
            fx.Store, fx.Options,
            TestPrincipalAccessors.WithScopes("orchestrator-attach", "orchestrator-admin"),
            server: null!, includeTerminal: true, includeAllSessions: true);

        result.IsError.Should().BeFalse();
        result.Data!.TotalKnown.Should().Be(2);
        result.Data.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListActiveInvestigations_LegacyFlag_LogsDeprecationWarningOnce()
    {
        // B5.3 (issue #184): when the legacy AllowCrossSessionAdmin flag is what
        // enables the bypass, the orchestrator logs a one-shot deprecation
        // warning. Multiple calls in the same process must still produce exactly
        // one warning so operator log streams stay clean.
        OrchestratorAdminBypassPolicy.ResetWarningLatchForTests();
        var fx = new Fixture { Options = { AllowCrossSessionAdmin = true } };
        fx.Store.Add(Active("a1") with { OwnerSessionId = "sess-other" });

        var capture = new CapturingLoggerFactory();
        for (var i = 0; i < 4; i++)
        {
            var result = await OrchestratorTools.ListActiveInvestigations(
                fx.Store, fx.Options, TestPrincipalAccessors.Root,
                server: null!, loggerFactory: capture,
                includeTerminal: true, includeAllSessions: true);
            result.IsError.Should().BeFalse();
        }

        capture.WarningMessages
            .Count(m => m.Contains("AllowCrossSessionAdmin is deprecated", StringComparison.Ordinal))
            .Should().Be(1, "deprecation warning is one-shot per process");
    }

    [Fact]
    public async Task ListActiveInvestigations_OrchestratorAdminScope_DoesNotLogDeprecation()
    {
        // The deprecation warning fires only on the legacy-flag path. A scope-only
        // bypass must leave the log stream clean.
        OrchestratorAdminBypassPolicy.ResetWarningLatchForTests();
        var fx = new Fixture();
        fx.Store.Add(Active("a1") with { OwnerSessionId = "sess-other" });
        var capture = new CapturingLoggerFactory();

        await OrchestratorTools.ListActiveInvestigations(
            fx.Store, fx.Options,
            TestPrincipalAccessors.WithScopes("orchestrator-attach", "orchestrator-admin"),
            server: null!, loggerFactory: capture,
            includeTerminal: true, includeAllSessions: true);

        capture.WarningMessages
            .Should().NotContain(m => m.Contains("AllowCrossSessionAdmin is deprecated", StringComparison.Ordinal));
    }

    private sealed class Fixture
    {
        public MemoryInvestigationStore Store { get; } = new();
        public MemoryInvestigationSessionBinder Binder { get; } = new();
        public NoopProxy Proxy { get; } = new();
        public NoopPortForward PortForward { get; } = new();
        public OrchestratorOptions Options { get; } = new();
        public OrchestratorObservability Observability { get; }
        public InvestigationCloser Closer { get; }

        public Fixture()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            var provider = services.BuildServiceProvider();
            Observability = new OrchestratorObservability(
                provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
                Store,
                new AuditLogWriter(TextWriter.Null));
            Closer = new InvestigationCloser(Store, Proxy, PortForward, Binder);
        }
    }

    private sealed class NoopProxy : IInvestigationProxyClient
    {
        public Task<CallToolResult> CallToolAsync(InvestigationHandle handle, CallToolRequestParams request, CancellationToken cancellationToken)
            => Task.FromResult(new CallToolResult());
        public Task DisposeForHandleAsync(string handleId) => Task.CompletedTask;
    }

    private sealed class NoopPortForward : IPortForwardManager
    {
        public Task<System.Net.Http.HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken)
            => Task.FromResult(new System.Net.Http.HttpClient());
        public Task CloseAsync(string handleId) => Task.CompletedTask;
    }

    private sealed class CapturingLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
    {
        public System.Collections.Concurrent.ConcurrentBag<string> WarningMessages { get; } = new();
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }

        private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
        {
            private readonly CapturingLoggerFactory _owner;
            public CapturingLogger(CapturingLoggerFactory owner) { _owner = owner; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
                    _owner.WarningMessages.Add(formatter(state, exception));
            }
        }
    }
}
