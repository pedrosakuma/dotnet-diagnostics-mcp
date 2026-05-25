using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Hosting;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using DotnetDiagnosticsMcp.Server.Security;
using DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator.Investigations;

/// <summary>
/// Narrow integration test for the <c>/proxy/{handleId}/...</c> reverse proxy. Spins up a
/// minimal <see cref="WebApplication"/> with stub <see cref="IInvestigationStore"/> /
/// <see cref="IPortForwardManager"/> + an upstream <c>HttpMessageHandler</c> that captures
/// the forwarded request. Validates the security boundary the LLM relies on: unknown handle
/// → 404, non-Active handle → 410, Active handle → swap external Authorization for the
/// Pod-local bearer and forward the body.
/// </summary>
[Collection(LegacyAdminBypassLatchCollection.Name)]
public class InvestigationProxyEndpointTests : IAsyncLifetime
{
    private IHost _host = default!;
    private TestServer _server = default!;
    private HttpClient _client = default!;
    private StubInvestigationStore _store = default!;
    private StubPortForwardManager _manager = default!;
    private CapturingUpstream _upstream = default!;

    public Task InitializeAsync() => InitializeAsync(proxyBytesCap: null);

    private async Task InitializeAsync(long? proxyBytesCap)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                _store = new StubInvestigationStore();
                _upstream = new CapturingUpstream();
                _manager = new StubPortForwardManager(_upstream);
                var opts = new OrchestratorOptions { Enabled = true, ProxyBasePath = "/proxy", ProxyPodPort = 5130 };
                if (proxyBytesCap.HasValue) opts.ProxyRequestSizeLimitBytes = proxyBytesCap.Value;
                services.AddSingleton(opts);
                services.AddSingleton<IInvestigationStore>(_store);
                services.AddSingleton<IPortForwardManager>(_manager);
                services.AddLogging();
                services.AddRouting();
                // The proxy endpoint chains .RequireRateLimiting("mcp") so the test
                // host must register a matching policy or the route is unreachable.
                services.AddRateLimiter(o =>
                {
                    o.AddPolicy(InvestigationProxyEndpoints.RateLimiterPolicyName, _ =>
                        System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test"));
                });
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseRateLimiter();
                app.UseEndpoints(e => e.MapInvestigationProxy());
            });
        });
        _host = await builder.StartAsync();
        _server = _host.GetTestServer();
        _client = _server.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Proxy_ReturnsNotFound_WhenHandleUnknown()
    {
        var response = await _client.PostAsync("/proxy/inv_missing/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Proxy_ReturnsGone_WhenHandleIsNotActive()
    {
        _store.Add(NewHandle("inv_attaching", InvestigationState.Attaching));
        var response = await _client.PostAsync("/proxy/inv_attaching/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Theory]
    [InlineData(InvestigationState.Closed)]
    [InlineData(InvestigationState.Expired)]
    [InlineData(InvestigationState.Failed)]
    public async Task Proxy_ReturnsGone_ForTerminalStates(InvestigationState state)
    {
        _store.Add(NewHandle("inv_term_" + state, state));
        var response = await _client.PostAsync($"/proxy/inv_term_{state}/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Proxy_StripsExternalAuthorization_AndInjectsPodLocalBearer()
    {
        const string pod = "pod-local-token-xyz";
        _store.Add(NewHandle("inv_ok", InvestigationState.Active, pod));
        _upstream.NextResponse = req =>
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            return msg;
        };

        var req = new HttpRequestMessage(HttpMethod.Post, "/proxy/inv_ok/mcp/tools/snapshot_counters?foo=bar")
        {
            Content = new StringContent("{\"durationSeconds\":3}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "external-bearer-from-llm");
        req.Headers.Add("X-Trace", "abc");

        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("ok");

        var forwarded = _upstream.LastRequest.Should().NotBeNull().And.Subject as HttpRequestMessage;
        forwarded!.Method.Should().Be(HttpMethod.Post);
        forwarded.RequestUri!.AbsolutePath.Should().Be("/mcp/tools/snapshot_counters");
        forwarded.RequestUri.Query.Should().Be("?foo=bar");
        forwarded.Headers.Authorization!.Scheme.Should().Be("Bearer");
        forwarded.Headers.Authorization.Parameter.Should().Be(pod);
        forwarded.Headers.GetValues("X-Trace").Should().ContainSingle().Which.Should().Be("abc");
        _upstream.LastRequestBody.Should().Be("{\"durationSeconds\":3}");
    }

    [Fact]
    public async Task Proxy_DoesNotForwardHopByHopHeaders()
    {
        _store.Add(NewHandle("inv_hop", InvestigationState.Active, "pod-token"));
        _upstream.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };

        var req = new HttpRequestMessage(HttpMethod.Post, "/proxy/inv_hop/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Connection", "keep-alive");
        req.Headers.TryAddWithoutValidation("Proxy-Authorization", "Basic abc");
        req.Headers.TryAddWithoutValidation("Cookie", "session=abc");

        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _upstream.LastRequest!.Headers.Contains("Connection").Should().BeFalse();
        _upstream.LastRequest.Headers.Contains("Proxy-Authorization").Should().BeFalse();
        _upstream.LastRequest.Headers.Contains("Cookie").Should().BeFalse();
        _upstream.LastRequest.Headers.Authorization!.Parameter.Should().Be("pod-token");
    }

    [Fact]
    public async Task Proxy_ReturnsBadGateway_WhenUpstreamThrows()
    {
        _store.Add(NewHandle("inv_502", InvestigationState.Active, "pod-token"));
        _upstream.NextResponse = _ => throw new HttpRequestException("connect refused");

        var response = await _client.PostAsync("/proxy/inv_502/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Proxy_RejectsCrossSessionCaller_WithStructured403()
    {
        // H6: handle minted with OwnerSessionId="owner-A", caller presents a different
        // Mcp-Session-Id → 403 ProxyOwnerMismatch.
        _store.Add(NewHandleOwned("inv_owned", "owner-A", "pod-token"));
        _upstream.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.OK);

        var req = new HttpRequestMessage(HttpMethod.Post, "/proxy/inv_owned/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Mcp-Session-Id", "owner-B");

        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ProxyOwnerMismatch");
        _upstream.LastRequest.Should().BeNull("the proxy must not forward when ownership check fails");
    }

    [Fact]
    public async Task Proxy_AdminBypass_AllowsCrossSessionCaller_WhenAllowCrossSessionAdminTrue()
    {
        // H6 + AllowCrossSessionAdmin: when the deployment opts into admin mode
        // (operator / central orchestrator topology), the owner-session check is
        // bypassed and the proxy forwards regardless of Mcp-Session-Id mismatch.
        await DisposeAsync();
        await InitializeAdminAsync();

        _store.Add(NewHandleOwned("inv_admin", "owner-A", "pod-token"));
        _upstream.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.OK);

        var req = new HttpRequestMessage(HttpMethod.Post, "/proxy/inv_admin/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Mcp-Session-Id", "owner-B");

        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _upstream.LastRequest.Should().NotBeNull("admin mode must forward cross-session traffic");
    }

    [Fact]
    public async Task Proxy_AdminBypass_AllowsCrossSessionCaller_WhenOrchestratorAdminScopeGranted()
    {
        // B5.3 (issue #184): the per-bearer 'orchestrator-admin' modifier scope is
        // the scope-first replacement for the AllowCrossSessionAdmin deployment
        // flag. With the flag OFF and the scope granted on the request principal,
        // the cross-session owner check must be bypassed exactly as before.
        await DisposeAsync();
        var adminPrincipal = new BearerPrincipal(
            name: "test-admin",
            scopes: System.Collections.Immutable.ImmutableHashSet.Create(
                "orchestrator-attach", "orchestrator-admin"));
        var capture = new CapturingLoggerProvider();
        await InitializeWithPrincipalAsync(adminPrincipal, allowCrossSessionAdmin: false, capture);

        _store.Add(NewHandleOwned("inv_scope_admin", "owner-A", "pod-token"));
        _upstream.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.OK);

        var req = new HttpRequestMessage(HttpMethod.Post, "/proxy/inv_scope_admin/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Mcp-Session-Id", "owner-B");

        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _upstream.LastRequest.Should().NotBeNull("orchestrator-admin scope must allow cross-session forwarding");
        capture.Messages.Should().NotContain(m => m.Contains("AllowCrossSessionAdmin is deprecated", StringComparison.Ordinal),
            "the scope path must not log the deprecation warning");
    }

    [Fact]
    public async Task Proxy_AdminBypass_LegacyFlag_LogsDeprecationWarningOnce()
    {
        // B5.3 (issue #184): when the legacy AllowCrossSessionAdmin flag is the
        // bypass path, the orchestrator logs a one-shot deprecation warning. The
        // warning must fire exactly once per process even across multiple bypass
        // requests, so the operator's log stream stays clean.
        OrchestratorAdminBypassPolicy.ResetWarningLatchForTests();
        await DisposeAsync();
        var capture = new CapturingLoggerProvider();
        await InitializeWithPrincipalAsync(principal: null, allowCrossSessionAdmin: true, capture);

        _store.Add(NewHandleOwned("inv_flag_dep", "owner-A", "pod-token"));
        _upstream.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.OK);

        for (var i = 0; i < 3; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/proxy/inv_flag_dep/mcp")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("Mcp-Session-Id", "owner-B");
            (await _client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        capture.Messages
            .Count(m => m.Contains("AllowCrossSessionAdmin is deprecated", StringComparison.Ordinal))
            .Should().Be(1, "deprecation warning is one-shot per process");
    }

    private async Task InitializeWithPrincipalAsync(
        BearerPrincipal? principal,
        bool allowCrossSessionAdmin,
        CapturingLoggerProvider? capture = null)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                _store = new StubInvestigationStore();
                _upstream = new CapturingUpstream();
                _manager = new StubPortForwardManager(_upstream);
                var opts = new OrchestratorOptions
                {
                    Enabled = true,
                    ProxyBasePath = "/proxy",
                    ProxyPodPort = 5130,
                    AllowCrossSessionAdmin = allowCrossSessionAdmin,
                };
                services.AddSingleton(opts);
                services.AddSingleton<IInvestigationStore>(_store);
                services.AddSingleton<IPortForwardManager>(_manager);
                services.AddLogging(b =>
                {
                    if (capture is not null) b.AddProvider(capture);
                });
                services.AddRouting();
                services.AddRateLimiter(o =>
                {
                    o.AddPolicy(InvestigationProxyEndpoints.RateLimiterPolicyName, _ =>
                        System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test"));
                });
            });
            web.Configure(app =>
            {
                if (principal is not null)
                {
                    app.Use(async (ctx, next) =>
                    {
                        ctx.SetBearerPrincipal(principal);
                        await next();
                    });
                }
                app.UseRouting();
                app.UseRateLimiter();
                app.UseEndpoints(e => e.MapInvestigationProxy());
            });
        });
        _host = await builder.StartAsync();
        _server = _host.GetTestServer();
        _client = _server.CreateClient();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            public CapturingLogger(CapturingLoggerProvider owner) { _owner = owner; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning) _owner.Messages.Add(formatter(state, exception));
            }
        }
    }

    private async Task InitializeAdminAsync()
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                _store = new StubInvestigationStore();
                _upstream = new CapturingUpstream();
                _manager = new StubPortForwardManager(_upstream);
                var opts = new OrchestratorOptions
                {
                    Enabled = true,
                    ProxyBasePath = "/proxy",
                    ProxyPodPort = 5130,
                    AllowCrossSessionAdmin = true,
                };
                services.AddSingleton(opts);
                services.AddSingleton<IInvestigationStore>(_store);
                services.AddSingleton<IPortForwardManager>(_manager);
                services.AddLogging();
                services.AddRouting();
                services.AddRateLimiter(o =>
                {
                    o.AddPolicy(InvestigationProxyEndpoints.RateLimiterPolicyName, _ =>
                        System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test"));
                });
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseRateLimiter();
                app.UseEndpoints(e => e.MapInvestigationProxy());
            });
        });
        _host = await builder.StartAsync();
        _server = _host.GetTestServer();
        _client = _server.CreateClient();
    }

    [Fact]
    public async Task Proxy_RejectsAnonymousCaller_WhenHandleHasOwner()
    {
        // H6: handle has an owner, caller omits the Mcp-Session-Id header entirely.
        _store.Add(NewHandleOwned("inv_owned_anon", "owner-A", "pod-token"));

        var response = await _client.PostAsync("/proxy/inv_owned_anon/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _upstream.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Proxy_AllowsOwnerSessionCaller()
    {
        _store.Add(NewHandleOwned("inv_owned_ok", "owner-A", "pod-token"));
        _upstream.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };

        var req = new HttpRequestMessage(HttpMethod.Post, "/proxy/inv_owned_ok/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Mcp-Session-Id", "owner-A");

        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _upstream.LastRequest.Should().NotBeNull();
    }

    [Fact]
    public async Task Proxy_RejectsDisallowedPath_WithStructured404()
    {
        // H7: any path under /proxy/{handleId}/ that isn't /mcp[...] is rejected
        // with a structured ProxyPathNotAllowed envelope.
        _store.Add(NewHandle("inv_path", InvestigationState.Active, "pod-token"));

        var response = await _client.GetAsync("/proxy/inv_path/health");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ProxyPathNotAllowed");
        _upstream.LastRequest.Should().BeNull();
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("OPTIONS")]
    public async Task Proxy_RejectsDisallowedMethods_OnMcpPath(string method)
    {
        // H7: only POST/GET/DELETE are valid on /mcp; everything else falls through
        // to the catch-all and returns ProxyPathNotAllowed.
        _store.Add(NewHandle("inv_method_" + method, InvestigationState.Active, "pod-token"));

        var req = new HttpRequestMessage(new HttpMethod(method), $"/proxy/inv_method_{method}/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _upstream.LastRequest.Should().BeNull();
    }

    [Theory]
    [InlineData("/proxy/inv_dot/mcp/../health")]
    [InlineData("/proxy/inv_dot/mcp/%2e%2e/health")]
    public async Task Proxy_RejectsDotSegmentPath_WithStructured404(string url)
    {
        // B3 review (issue #164 High 3): dot-segments must be rejected before
        // they can be normalized away by UriBuilder and escape /mcp.
        _store.Add(NewHandle("inv_dot", InvestigationState.Active, "pod-token"));

        var response = await _client.PostAsync(url, new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ProxyPathNotAllowed");
        _upstream.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Proxy_RejectsDisallowedJsonRpcTool_With403()
    {
        // B3 review (issue #164 High 1): a direct POST to /proxy/{id}/mcp must
        // be blocked by the same allowlist that gates the in-process call-tool
        // filter — otherwise the HTTP proxy is a bypass.
        _store.Add(NewHandle("inv_jrpc_bad", InvestigationState.Active, "pod-token"));
        _upstream.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.OK);

        var payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"totally_not_a_real_tool\",\"arguments\":{}}}";
        var response = await _client.PostAsync("/proxy/inv_jrpc_bad/mcp", new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ProxyToolNotAllowed");
        _upstream.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Proxy_AllowsJsonRpcInitialize_Passthrough()
    {
        // Non-tools/call methods must pass through unaltered — the allowlist
        // gate is scoped to tools/call envelopes only.
        _store.Add(NewHandle("inv_jrpc_init", InvestigationState.Active, "pod-token"));
        _upstream.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };

        var payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}";
        var response = await _client.PostAsync("/proxy/inv_jrpc_init/mcp", new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _upstream.LastRequest.Should().NotBeNull();
    }

    [Fact]
    public async Task Proxy_AllowsJsonRpcKnownTool_Passthrough()
    {
        _store.Add(NewHandle("inv_jrpc_ok", InvestigationState.Active, "pod-token"));
        _upstream.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };

        var allowed = Server.Orchestrator.Investigations.InvestigationProxyToolAllowlist.AllowedToolNames.First();
        var payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + allowed + "\",\"arguments\":{}}}";
        var response = await _client.PostAsync("/proxy/inv_jrpc_ok/mcp", new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _upstream.LastRequest.Should().NotBeNull();
    }

    [Fact]
    public async Task Proxy_RejectsOversizedBody_With413()
    {
        // M5: body cap = 1 MiB by default; this test pins the cap small and sends
        // a payload over the limit. Expect 413 + ProxyBodyTooLarge envelope.
        // We rebuild the host with a tiny cap so the test is fast.
        await DisposeAsync();
        await InitializeAsync(proxyBytesCap: 1024);

        _store.Add(NewHandle("inv_big", InvestigationState.Active, "pod-token"));
        _upstream.NextResponse = _ => new HttpResponseMessage(HttpStatusCode.OK);

        var payload = new string('x', 4096);
        var response = await _client.PostAsync("/proxy/inv_big/mcp", new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ProxyBodyTooLarge");
        _upstream.LastRequest.Should().BeNull();
    }

    private static InvestigationHandle NewHandle(string id, InvestigationState state, string podToken = "pod") => new(
        HandleId: id,
        Namespace: "ns",
        PodName: "pod",
        TargetContainerName: "app",
        EphemeralContainerName: "diag",
        PodLocalBearerToken: podToken,
        State: state,
        AttachedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5));

    private static InvestigationHandle NewHandleOwned(string id, string ownerSessionId, string podToken) => new(
        HandleId: id,
        Namespace: "ns",
        PodName: "pod",
        TargetContainerName: "app",
        EphemeralContainerName: "diag",
        PodLocalBearerToken: podToken,
        State: InvestigationState.Active,
        AttachedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
        OwnerSessionId: ownerSessionId);

    private sealed class StubInvestigationStore : IInvestigationStore
    {
        private readonly ConcurrentDictionary<string, InvestigationHandle> _byId = new(StringComparer.Ordinal);
        public void Add(InvestigationHandle handle) => _byId[handle.HandleId] = handle;
        public bool TryReserveTarget(InvestigationHandle newHandle, bool allowReuse, out InvestigationHandle? existing)
        { existing = null; _byId[newHandle.HandleId] = newHandle; return true; }
        public void Update(InvestigationHandle handle) => _byId[handle.HandleId] = handle;
        public InvestigationHandle? GetById(string id) => _byId.TryGetValue(id, out var h) ? h : null;
        public InvestigationTerminalTransition TryTransitionToTerminal(string handleId, InvestigationState targetState, string? failureReason, out InvestigationState? previousState)
        {
            previousState = null;
            if (!_byId.TryGetValue(handleId, out var current)) return InvestigationTerminalTransition.NotFound;
            previousState = current.State;
            if (current.State is InvestigationState.Closed or InvestigationState.Expired or InvestigationState.Failed) return InvestigationTerminalTransition.AlreadyTerminal;
            _byId[handleId] = current with { State = targetState, FailureReason = targetState == InvestigationState.Closed ? current.FailureReason : failureReason ?? current.FailureReason };
            return InvestigationTerminalTransition.Transitioned;
        }
        public InvestigationHandle? FindReusableTarget(string ns, string pod, string c) => null;
        public System.Collections.Generic.IReadOnlyCollection<InvestigationHandle> Snapshot() => _byId.Values.ToArray();
    }

    private sealed class StubPortForwardManager : IPortForwardManager
    {
        private readonly HttpClient _client;
        public StubPortForwardManager(CapturingUpstream upstream)
        {
            _client = new HttpClient(upstream) { BaseAddress = new Uri("http://pod-local") };
        }
        public Task<HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken ct) => Task.FromResult(_client);
        public Task CloseAsync(string handleId) => Task.CompletedTask;
    }

    private sealed class CapturingUpstream : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastRequestBody;
        public Func<HttpRequestMessage, HttpResponseMessage> NextResponse { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastRequest = request;
            return NextResponse(request);
        }
    }
}
