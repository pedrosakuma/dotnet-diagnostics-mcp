using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DotnetDiagnosticsMcp.Core.Drilldown;

/// <summary>
/// Simple in-memory <see cref="IDiagnosticHandleStore"/>. Uses a concurrent dictionary plus a
/// LRU-ish eviction pass at registration time. Suitable for a single sidecar; not designed for
/// horizontal scale (handles are scoped to one process).
/// </summary>
public sealed class MemoryDiagnosticHandleStore : IDiagnosticHandleStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private readonly TimeProvider _clock;

    public MemoryDiagnosticHandleStore(int maxEntries = 64, TimeProvider? clock = null)
    {
        if (maxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Must be >= 1.");
        }

        _maxEntries = maxEntries;
        _clock = clock ?? TimeProvider.System;
    }

    public DiagnosticHandle Register(int processId, string kind, object artifact, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");
        }

        EvictExpired();
        EnforceCapacity();

        var id = NewHandleId();
        var expiresAt = _clock.GetUtcNow().Add(ttl);
        var handle = new DiagnosticHandle(id, expiresAt, processId, kind);
        _entries[id] = new Entry(handle, artifact);
        return handle;
    }

    public T? TryGet<T>(string handle) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        if (!_entries.TryGetValue(handle, out var entry))
        {
            return null;
        }

        if (entry.Handle.ExpiresAt <= _clock.GetUtcNow())
        {
            _entries.TryRemove(handle, out _);
            DisposeIfNeeded(entry.Artifact);
            return null;
        }

        return entry.Artifact as T;
    }

    public bool Invalidate(string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        if (_entries.TryRemove(handle, out var entry))
        {
            DisposeIfNeeded(entry.Artifact);
            return true;
        }

        return false;
    }

    public int InvalidateForProcess(int processId)
    {
        var victims = _entries
            .Where(kv => kv.Value.Handle.ProcessId == processId)
            .Select(kv => kv.Key)
            .ToArray();

        var removed = 0;
        foreach (var key in victims)
        {
            if (_entries.TryRemove(key, out var entry))
            {
                DisposeIfNeeded(entry.Artifact);
                removed++;
            }
        }

        return removed;
    }

    private void EvictExpired()
    {
        var now = _clock.GetUtcNow();
        foreach (var kv in _entries)
        {
            if (kv.Value.Handle.ExpiresAt <= now && _entries.TryRemove(kv.Key, out var removed))
            {
                DisposeIfNeeded(removed.Artifact);
            }
        }
    }

    private void EnforceCapacity()
    {
        while (_entries.Count >= _maxEntries)
        {
            var oldest = _entries
                .OrderBy(kv => kv.Value.Handle.ExpiresAt)
                .Select(kv => kv.Key)
                .FirstOrDefault();
            if (oldest is null || !_entries.TryRemove(oldest, out var removed))
            {
                return;
            }

            DisposeIfNeeded(removed.Artifact);
        }
    }

    private static void DisposeIfNeeded(object artifact)
    {
        if (artifact is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { /* best-effort */ }
        }
    }

    private static string NewHandleId()
    {
        // 96-bit random, base32-encoded (no padding) — short, URL-safe, ambiguity-free.
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        return ToCrockford(bytes);
    }

    private static readonly char[] Crockford =
    {
        '0','1','2','3','4','5','6','7','8','9',
        'A','B','C','D','E','F','G','H','J','K','M','N','P','Q','R','S','T','V','W','X','Y','Z',
    };

    private static string ToCrockford(ReadOnlySpan<byte> bytes)
    {
        // 12 bytes = 96 bits = 20 base32 chars (rounded up; we encode 96/5 = 19.2 -> 20 chars, pad-free).
        var chars = new char[20];
        int bitBuffer = 0;
        int bitCount = 0;
        int outIndex = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            bitBuffer = (bitBuffer << 8) | bytes[i];
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                chars[outIndex++] = Crockford[(bitBuffer >> bitCount) & 0x1F];
            }
        }
        if (bitCount > 0)
        {
            chars[outIndex++] = Crockford[(bitBuffer << (5 - bitCount)) & 0x1F];
        }
        return new string(chars, 0, outIndex);
    }

    private sealed record Entry(DiagnosticHandle Handle, object Artifact);
}
