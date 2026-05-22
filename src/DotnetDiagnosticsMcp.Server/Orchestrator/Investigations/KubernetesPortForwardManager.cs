using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using k8s;
using k8s.Autorest;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;

/// <summary>
/// Default <see cref="IPortForwardManager"/>: opens an in-process Kubernetes port-forward
/// WebSocket per outbound TCP connection, multiplexed via <see cref="IStreamDemuxer"/>.
/// The data-plane stream is plugged into <see cref="SocketsHttpHandler.ConnectCallback"/>
/// so the proxy endpoint can use a plain <see cref="HttpClient"/> with no awareness of
/// kube transport details.
/// </summary>
/// <remarks>
/// <para>
/// Each handle gets ONE shared <see cref="HttpClient"/>. HttpClient pools connections per
/// host; pooled connections survive until the handler is disposed or the pod-local server
/// closes them. On the cold path (no pooled connection), <c>ConnectCallback</c> fires a
/// fresh <c>WebSocketNamespacedPodPortForwardAsync</c> call and exposes channel 0 of the
/// demuxer as the connection stream.
/// </para>
/// </remarks>
internal sealed class KubernetesPortForwardManager : IPortForwardManager, IAsyncDisposable
{
    private readonly IKubernetesPodsApi _podsApi;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<KubernetesPortForwardManager> _logger;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public KubernetesPortForwardManager(
        IKubernetesPodsApi podsApi,
        OrchestratorOptions options,
        ILogger<KubernetesPortForwardManager> logger)
    {
        _podsApi = podsApi;
        _options = options;
        _logger = logger;
    }

    public Task<HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_gate)
        {
            if (_entries.TryGetValue(handle.HandleId, out var existing))
            {
                return Task.FromResult(existing.Client);
            }

            var entry = BuildEntry(handle);
            _entries[handle.HandleId] = entry;
            _logger.LogInformation(
                "Port-forward transport registered for investigation {HandleId} → {Namespace}/{Pod}:{Port}.",
                handle.HandleId, handle.Namespace, handle.PodName, _options.ProxyPodPort);
            return Task.FromResult(entry.Client);
        }
    }

    public Task CloseAsync(string handleId)
    {
        Entry? removed;
        lock (_gate)
        {
            if (!_entries.Remove(handleId, out removed)) return Task.CompletedTask;
        }
        SafeDispose(removed!, handleId);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        List<KeyValuePair<string, Entry>> snapshot;
        lock (_gate)
        {
            snapshot = new List<KeyValuePair<string, Entry>>(_entries);
            _entries.Clear();
        }
        foreach (var kv in snapshot)
        {
            SafeDispose(kv.Value, kv.Key);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void SafeDispose(Entry entry, string handleId)
    {
        try
        {
            entry.Cts.Cancel();
        }
        catch (ObjectDisposedException) { }
        try
        {
            entry.Client.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing port-forward HttpClient for {HandleId} threw.", handleId);
        }
        try
        {
            entry.Cts.Dispose();
        }
        catch (ObjectDisposedException) { }
    }

    private Entry BuildEntry(InvestigationHandle handle)
    {
        var cts = new CancellationTokenSource();
        var podPort = _options.ProxyPodPort;
        var handler = new SocketsHttpHandler
        {
            // Stream-based transport; the orchestrator process owns the lifetime of every
            // port-forward WebSocket and we don't want HttpClient retrying SocketException
            // on a stream that was killed by the cluster-side WS close.
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            // The port-forward demuxer can leave the WS-backed connection in a half-closed
            // state when the pod-side server closes its read side but our reader hasn't yet
            // observed EOF. Reusing such a connection deadlocks the next write. Force a
            // fresh port-forward open per request — the cost is one extra round-trip to
            // the kube-apiserver, well under 100ms in-cluster.
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = (ctx, ct) => OpenPortForwardStreamAsync(handle, podPort, cts.Token, ct),
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            // Synthetic host — the real routing happens via ConnectCallback. The path is
            // appended by the proxy endpoint when building the inner request URL.
            BaseAddress = new Uri("http://pod-local"),
            // Generous so long-running diagnostic collections (CPU sampler, GC events,
            // EventSource passthrough) don't trip the default 100s HttpClient timeout.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        return new Entry(client, cts);
    }

    private async ValueTask<Stream> OpenPortForwardStreamAsync(
        InvestigationHandle handle,
        int podPort,
        CancellationToken managerCt,
        CancellationToken requestCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(managerCt, requestCt);
        IStreamDemuxer demuxer;
        try
        {
            demuxer = await _podsApi.OpenPortForwardAsync(handle.Namespace, handle.PodName, podPort, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (managerCt.IsCancellationRequested)
        {
            throw new IOException("Port-forward transport was closed while opening a new connection.");
        }
        catch (HttpOperationException ex)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.PortForwardFailed,
                $"Kubernetes API rejected the port-forward upgrade for {handle.Namespace}/{handle.PodName}:{podPort} " +
                $"with {(int?)ex.Response?.StatusCode}: {ex.Message}.", ex);
        }
        catch (Exception ex) when (ex is not OrchestratorException)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.PortForwardFailed,
                $"Failed to open port-forward to {handle.Namespace}/{handle.PodName}:{podPort}: {ex.Message}.", ex);
        }

        var dataStream = demuxer.GetStream((byte?)0, (byte?)0);
        return new DemuxerOwnedStream(dataStream, demuxer);
    }

    private sealed record Entry(HttpClient Client, CancellationTokenSource Cts);

    /// <summary>
    /// Wraps the per-port data stream so disposing it also closes the underlying
    /// <see cref="IStreamDemuxer"/> (which in turn closes the WebSocket). HttpClient's
    /// connection pool disposes the connection stream when it evicts a pooled
    /// connection — this keeps the WS lifetime tied to the connection lifetime.
    /// </summary>
    private sealed class DemuxerOwnedStream : Stream
    {
        private readonly Stream _inner;
        private readonly IStreamDemuxer _demuxer;
        private int _disposed;

        public DemuxerOwnedStream(Stream inner, IStreamDemuxer demuxer)
        {
            _inner = inner;
            _demuxer = demuxer;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            if (disposing)
            {
                try { _inner.Dispose(); } catch { /* best-effort */ }
                try { _demuxer.Dispose(); } catch { /* best-effort */ }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                await base.DisposeAsync().ConfigureAwait(false);
                return;
            }
            try { await _inner.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            try { _demuxer.Dispose(); } catch { /* best-effort */ }
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
