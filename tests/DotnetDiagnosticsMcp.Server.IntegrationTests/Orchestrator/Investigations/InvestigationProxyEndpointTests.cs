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

    public async Task InitializeAsync()
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
                services.AddSingleton(new OrchestratorOptions { Enabled = true, ProxyBasePath = "/proxy", ProxyPodPort = 5130 });
                services.AddSingleton<IInvestigationStore>(_store);
                services.AddSingleton<IPortForwardManager>(_manager);
                services.AddLogging();
                services.AddRouting();
            });
            web.Configure(app =>
            {
                app.UseRouting();
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
        var response = await _client.GetAsync("/proxy/inv_missing/health");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Proxy_ReturnsGone_WhenHandleIsNotActive()
    {
        _store.Add(NewHandle("inv_attaching", InvestigationState.Attaching));
        var response = await _client.GetAsync("/proxy/inv_attaching/health");
        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Theory]
    [InlineData(InvestigationState.Closed)]
    [InlineData(InvestigationState.Expired)]
    [InlineData(InvestigationState.Failed)]
    public async Task Proxy_ReturnsGone_ForTerminalStates(InvestigationState state)
    {
        _store.Add(NewHandle("inv_term_" + state, state));
        var response = await _client.GetAsync($"/proxy/inv_term_{state}/health");
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

        var req = new HttpRequestMessage(HttpMethod.Get, "/proxy/inv_hop/x");
        req.Headers.TryAddWithoutValidation("Connection", "keep-alive");
        req.Headers.TryAddWithoutValidation("Proxy-Authorization", "Basic abc");

        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _upstream.LastRequest!.Headers.Contains("Connection").Should().BeFalse();
        _upstream.LastRequest.Headers.Contains("Proxy-Authorization").Should().BeFalse();
        _upstream.LastRequest.Headers.Authorization!.Parameter.Should().Be("pod-token");
    }

    [Fact]
    public async Task Proxy_ReturnsBadGateway_WhenUpstreamThrows()
    {
        _store.Add(NewHandle("inv_502", InvestigationState.Active, "pod-token"));
        _upstream.NextResponse = _ => throw new HttpRequestException("connect refused");

        var response = await _client.GetAsync("/proxy/inv_502/x");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
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

    private sealed class StubInvestigationStore : IInvestigationStore
    {
        private readonly ConcurrentDictionary<string, InvestigationHandle> _byId = new(StringComparer.Ordinal);
        public void Add(InvestigationHandle handle) => _byId[handle.HandleId] = handle;
        public bool TryReserveTarget(InvestigationHandle newHandle, bool allowReuse, out InvestigationHandle? existing)
        { existing = null; _byId[newHandle.HandleId] = newHandle; return true; }
        public void Update(InvestigationHandle handle) => _byId[handle.HandleId] = handle;
        public InvestigationHandle? GetById(string id) => _byId.TryGetValue(id, out var h) ? h : null;
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
