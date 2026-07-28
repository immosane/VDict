namespace VDict.Core.Policies;

public sealed class SizedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly long _capacity;
    private readonly long _maximumItemSize;
    private readonly Func<TValue, long> _sizeOf;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = [];
    private readonly object _sync = new();
    private long _size;

    public SizedLruCache(long capacity, long maximumItemSize, Func<TValue, long> sizeOf)
    {
        _capacity = Math.Max(0, capacity);
        _maximumItemSize = Math.Max(0, maximumItemSize);
        _sizeOf = sizeOf;
    }

    public long Size
    {
        get
        {
            lock (_sync)
            {
                return _size;
            }
        }
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                value = default;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Set(TKey key, TValue value)
    {
        long itemSize = Math.Max(0, _sizeOf(value));
        if (_capacity == 0 || itemSize > _maximumItemSize || itemSize > _capacity)
        {
            return;
        }

        lock (_sync)
        {
            if (_entries.Remove(key, out LinkedListNode<Entry>? existing))
            {
                _lru.Remove(existing);
                _size -= existing.Value.Size;
            }

            LinkedListNode<Entry> node = new(new Entry(key, value, itemSize));
            _lru.AddFirst(node);
            _entries[key] = node;
            _size += itemSize;
            while (_size > _capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _entries.Remove(last.Value.Key);
                _size -= last.Value.Size;
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _lru.Clear();
            _size = 0;
        }
    }

    private sealed record Entry(TKey Key, TValue Value, long Size);
}
