using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using DotnetDiagnosticsMcp.Server.Azure;

namespace DotnetDiagnosticsMcp.Server.Orchestrator;

/// <summary>
/// Default <see cref="IKubeconfigHandleStore"/> implementation: an in-memory
/// dictionary guarded by a single lock, with TTL-based eviction and zero-on-expire.
/// Singleton lifetime — one store per server process.
/// </summary>
/// <remarks>
/// <para>
/// The store deliberately keeps a coarse single-lock design rather than a
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>.
/// Volume is small (a handful of clusters per investigation, capped at TTL) and
/// the lock makes the "zero-then-remove" expiry path race-free without explicit
/// memory ordering.
/// </para>
/// <para>
/// Handle ids are derived from 16 bytes of <see cref="RandomNumberGenerator"/>
/// output and rendered as 32-char lowercase hex with a <c>kc:</c> prefix. That
/// gives ~128 bits of unguessable entropy — comfortably above the bearer-token
/// strength the rest of the server relies on.
/// </para>
/// </remarks>
internal sealed class InMemoryKubeconfigHandleStore : IKubeconfigHandleStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;

    public InMemoryKubeconfigHandleStore(AzureDiscoveryOptions? options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        var configured = options?.KubeconfigHandleTtl ?? TimeSpan.Zero;
        _ttl = configured > TimeSpan.Zero ? configured : TimeSpan.FromMinutes(10);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                EvictExpired_NoLock();
                return _entries.Count;
            }
        }
    }

    public KubeconfigHandleMint Register(byte[] kubeconfig)
    {
        ArgumentNullException.ThrowIfNull(kubeconfig);

        var handle = MintHandleId();
        var expiresAt = _clock.GetUtcNow() + _ttl;

        lock (_gate)
        {
            EvictExpired_NoLock();
            _entries[handle] = new Entry(kubeconfig, expiresAt);
        }

        return new KubeconfigHandleMint(handle, expiresAt);
    }

    public byte[]? TryResolve(string handle)
    {
        if (string.IsNullOrEmpty(handle)) return null;

        lock (_gate)
        {
            EvictExpired_NoLock();
            if (!_entries.TryGetValue(handle, out var entry)) return null;

            // Defensive copy: callers MUST NOT mutate or retain a reference into
            // the store's buffer. The store owns the lifecycle (including the
            // final Array.Clear on expiry).
            var copy = new byte[entry.Bytes.Length];
            Buffer.BlockCopy(entry.Bytes, 0, copy, 0, entry.Bytes.Length);
            return copy;
        }
    }

    private void EvictExpired_NoLock()
    {
        var now = _clock.GetUtcNow();
        List<string>? doomed = null;
        foreach (var kv in _entries)
        {
            if (kv.Value.ExpiresAt <= now)
            {
                doomed ??= new List<string>();
                doomed.Add(kv.Key);
            }
        }
        if (doomed is null) return;

        foreach (var key in doomed)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                // Zero the bytes BEFORE releasing the dictionary reference so a
                // GC-walker / heap-dump captures only zeros, not the kubeconfig.
                Array.Clear(entry.Bytes, 0, entry.Bytes.Length);
                _entries.Remove(key);
            }
        }
    }

    private static string MintHandleId()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return "kc:" + Convert.ToHexString(buffer).ToLowerInvariant();
    }

    private readonly record struct Entry(byte[] Bytes, DateTimeOffset ExpiresAt);
}
