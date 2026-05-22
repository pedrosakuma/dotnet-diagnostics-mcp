using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using FluentAssertions;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator.Investigations;

public class KubernetesPortForwardManagerTests
{
    [Fact]
    public async Task GetOrCreateClientAsync_RoundTripsThroughDemuxerStream()
    {
        await using var loopback = new LoopbackHttpServer();
        loopback.Start();

        var podsApi = new StubPodsApi(loopback);
        var options = new OrchestratorOptions { Enabled = true, ProxyPodPort = 5130 };
        await using var manager = new KubernetesPortForwardManager(podsApi, options, NullLogger<KubernetesPortForwardManager>.Instance);

        var handle = NewHandle();
        var client = await manager.GetOrCreateClientAsync(handle, CancellationToken.None);
        var response = await client.GetAsync("/health");

        response.IsSuccessStatusCode.Should().BeTrue();
        (await response.Content.ReadAsStringAsync()).Should().Be("OK");
        podsApi.OpenCount.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task GetOrCreateClientAsync_ReturnsSameClient_ForSameHandle()
    {
        await using var loopback = new LoopbackHttpServer();
        loopback.Start();
        var podsApi = new StubPodsApi(loopback);
        var options = new OrchestratorOptions { Enabled = true, ProxyPodPort = 5130 };
        await using var manager = new KubernetesPortForwardManager(podsApi, options, NullLogger<KubernetesPortForwardManager>.Instance);
        var handle = NewHandle();

        var c1 = await manager.GetOrCreateClientAsync(handle, CancellationToken.None);
        var c2 = await manager.GetOrCreateClientAsync(handle, CancellationToken.None);

        c1.Should().BeSameAs(c2);
    }

    [Fact]
    public async Task CloseAsync_DisposesClient_AndIsIdempotent()
    {
        await using var loopback = new LoopbackHttpServer();
        loopback.Start();
        var podsApi = new StubPodsApi(loopback);
        var options = new OrchestratorOptions { Enabled = true, ProxyPodPort = 5130 };
        var manager = new KubernetesPortForwardManager(podsApi, options, NullLogger<KubernetesPortForwardManager>.Instance);
        var handle = NewHandle();

        var client = await manager.GetOrCreateClientAsync(handle, CancellationToken.None);
        await manager.CloseAsync(handle.HandleId);
        await manager.CloseAsync(handle.HandleId); // idempotent

        Func<Task> act = async () => await client.GetAsync("/health");
        await act.Should().ThrowAsync<ObjectDisposedException>();

        await manager.DisposeAsync();
    }

    private static InvestigationHandle NewHandle() => new(
        HandleId: "inv_" + Guid.NewGuid().ToString("N"),
        Namespace: "ns",
        PodName: "pod",
        TargetContainerName: "app",
        EphemeralContainerName: "diag",
        PodLocalBearerToken: "token",
        State: InvestigationState.Active,
        AttachedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10));

    /// <summary>
    /// Stub <see cref="IKubernetesPodsApi"/> whose port-forward returns an
    /// <see cref="IStreamDemuxer"/> that proxies channel 0 to a real TCP socket on the
    /// supplied <see cref="LoopbackHttpServer"/>. That is the closest we can get to an
    /// integration test of the demuxer → SocketsHttpHandler.ConnectCallback wiring
    /// without spinning a real Kubernetes API server.
    /// </summary>
    private sealed class StubPodsApi : IKubernetesPodsApi
    {
        private readonly LoopbackHttpServer _loopback;
        public int OpenCount;
        public StubPodsApi(LoopbackHttpServer loopback) { _loopback = loopback; }

        public Task<V1PodList> ListPodsAsync(string? n, string? l, string? f, int? limit, string? cont, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<V1Pod> ReadPodAsync(string n, string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<V1Pod> AddEphemeralContainerAsync(string n, string name, V1EphemeralContainer e, CancellationToken ct) => throw new NotSupportedException();

        public async Task<IStreamDemuxer> OpenPortForwardAsync(string n, string name, int port, CancellationToken ct)
        {
            Interlocked.Increment(ref OpenCount);
            var socket = new TcpClient();
            await socket.ConnectAsync(_loopback.Endpoint.Address, _loopback.Endpoint.Port, ct);
            return new TcpBackedDemuxer(socket);
        }
    }

    /// <summary>
    /// Implements just enough of <see cref="IStreamDemuxer"/> for the manager:
    /// <c>GetStream(0,0)</c> returns the TCP socket stream, disposing either closes both.
    /// </summary>
    private sealed class TcpBackedDemuxer : IStreamDemuxer
    {
        private readonly TcpClient _tcp;
        public TcpBackedDemuxer(TcpClient tcp) { _tcp = tcp; }
        public event EventHandler? ConnectionClosed { add { } remove { } }
        public void Start() { }
        public Stream GetStream(ChannelIndex? inputIndex, ChannelIndex? outputIndex) => _tcp.GetStream();
        public Stream GetStream(byte? inputIndex, byte? outputIndex) => _tcp.GetStream();
        public Task Write(ChannelIndex index, byte[] buffer, int offset, int count, CancellationToken ct = default)
            => _tcp.GetStream().WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public Task Write(byte index, byte[] buffer, int offset, int count, CancellationToken ct = default)
            => _tcp.GetStream().WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public void Dispose() => _tcp.Dispose();
    }

    /// <summary>Minimal HTTP/1.1 loopback server that replies "OK" to any request.</summary>
    private sealed class LoopbackHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public LoopbackHttpServer() { _listener = new TcpListener(System.Net.IPAddress.Loopback, 0); }
        public System.Net.IPEndPoint Endpoint => (System.Net.IPEndPoint)_listener.LocalEndpoint;

        public void Start()
        {
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => HandleAsync(client));
                }
            }
            catch (OperationCanceledException) { }
        }

        private static async Task HandleAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buf = new byte[4096];
                    // Read until we see CRLFCRLF — enough for a GET /health request.
                    var read = await stream.ReadAsync(buf);
                    if (read <= 0) return;
                    var body = "OK";
                    var resp = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n{body}";
                    var bytes = Encoding.ASCII.GetBytes(resp);
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                }
            }
            catch { /* test loopback — swallow */ }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            if (_loop is not null)
            {
                try { await _loop; } catch { }
            }
            _cts.Dispose();
        }
    }
}
