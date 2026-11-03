namespace RhoViewerPortable;

/// <summary>
/// Thread-safe least-recently-used byte cache with byte and entry-count limits.
/// Cached arrays are treated as immutable and returned without copying.
/// </summary>
internal sealed class BoundedByteCache<TKey> where TKey : notnull
{
    private sealed record Entry(TKey Key, byte[] Bytes);

    private readonly object _sync = new();
    private readonly long _capacityBytes;
    private readonly int _maximumEntries;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _mostRecentFirst = new();
    private long _cachedBytes;

    public BoundedByteCache(long capacityBytes, int maximumEntries)
    {
        if (capacityBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        if (maximumEntries < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        _capacityBytes = capacityBytes;
        _maximumEntries = maximumEntries;
    }

    internal long CachedBytes
    {
        get
        {
            lock (_sync)
                return _cachedBytes;
        }
    }

    internal int Count
    {
        get
        {
            lock (_sync)
                return _entries.Count;
        }
    }

    internal bool TryGetValue(TKey key, out byte[] bytes)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                bytes = Array.Empty<byte>();
                return false;
            }

            _mostRecentFirst.Remove(node);
            _mostRecentFirst.AddFirst(node);
            bytes = node.Value.Bytes;
            return true;
        }
    }

    internal void Add(TKey key, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (_capacityBytes == 0 || _maximumEntries == 0 || bytes.LongLength > _capacityBytes)
            return;

        lock (_sync)
        {
            RemoveIfPresent(key);
            while (_entries.Count >= _maximumEntries || _cachedBytes > _capacityBytes - bytes.LongLength)
                EvictLeastRecent();

            var node = _mostRecentFirst.AddFirst(new Entry(key, bytes));
            _entries.Add(key, node);
            _cachedBytes += bytes.LongLength;
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _mostRecentFirst.Clear();
            _cachedBytes = 0;
        }
    }

    private void RemoveIfPresent(TKey key)
    {
        if (!_entries.TryGetValue(key, out var node))
            return;

        _entries.Remove(key);
        _mostRecentFirst.Remove(node);
        _cachedBytes -= node.Value.Bytes.LongLength;
    }

    private void EvictLeastRecent()
    {
        var node = _mostRecentFirst.Last;
        if (node is null)
            return;

        _mostRecentFirst.RemoveLast();
        _entries.Remove(node.Value.Key);
        _cachedBytes -= node.Value.Bytes.LongLength;
    }
}
