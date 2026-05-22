using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Hosting;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
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
