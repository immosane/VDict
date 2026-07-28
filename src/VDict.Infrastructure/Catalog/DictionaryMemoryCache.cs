using VDict.Core.Models;

namespace VDict.Infrastructure.Catalog;

internal sealed class DictionaryMemoryCache
{
    internal const long Megabyte = 1024L * 1024L;
    internal const long TotalCapacityBytes = 128L * Megabyte;
    internal const long RecordBlockCapacityBytes = 96L * Megabyte;
    internal const long DecodedResourceCapacityBytes = 32L * Megabyte;

    private static readonly TimeSpan MissingResourceLifetime = TimeSpan.FromSeconds(30);
    private const int MaximumMissingResources = 1024;
    private readonly FairSizedLruCache<DictionaryId, RecordBlockCacheKey, byte[]> _recordBlocks =
        new(
            RecordBlockCapacityBytes,
            8L * Megabyte,
            4L * Megabyte,
            value => value.LongLength);
    private readonly FairSizedLruCache<DictionaryId, DecodedResourceCacheKey, ResourceContent>
        _decodedResources =
            new(
                DecodedResourceCapacityBytes,
                2L * Megabyte,
                1L * Megabyte,
                value => value.Body.Length);
    private readonly Dictionary<OwnedMissingResourceKey, DateTimeOffset> _missingResources = [];
    private readonly object _missingSync = new();
    private long _missingHits;
    private long _missingMisses;
    private long _generation;

    public long Generation => Interlocked.Read(ref _generation);

    public void AdvanceGeneration(long generation, IEnumerable<DictionaryId> activeDictionaries)
    {
        DictionaryId[] owners = activeDictionaries.Distinct().ToArray();
        _recordBlocks.Reset(owners);
        _decodedResources.Reset(owners);
        lock (_missingSync)
        {
            _missingResources.Clear();
        }

        Interlocked.Exchange(ref _generation, generation);
    }

    public bool TryGetRecordBlock(
        DictionaryId dictionaryId,
        RecordBlockCacheKey key,
        out byte[]? block) =>
        _recordBlocks.TryGet(dictionaryId, key, out block);

    public void StoreRecordBlock(
        DictionaryId dictionaryId,
        RecordBlockCacheKey key,
        byte[] block) =>
        _recordBlocks.Set(dictionaryId, key, block);

    public bool TryGetDecodedResource(
        DictionaryId dictionaryId,
        DecodedResourceCacheKey key,
        out ResourceContent? resource) =>
        _decodedResources.TryGet(dictionaryId, key, out resource);

    public void StoreDecodedResource(
        DictionaryId dictionaryId,
        DecodedResourceCacheKey key,
        ResourceContent resource)
    {
        _decodedResources.Set(dictionaryId, key, resource);
        RemoveMissing(dictionaryId, key);
    }

    public bool IsKnownMissing(
        DictionaryId dictionaryId,
        DecodedResourceCacheKey key,
        DateTimeOffset? now = null)
    {
        DateTimeOffset current = now ?? DateTimeOffset.UtcNow;
        OwnedMissingResourceKey owned = new(dictionaryId, key);
        lock (_missingSync)
        {
            if (_missingResources.TryGetValue(owned, out DateTimeOffset expiresAt))
            {
                if (expiresAt > current)
                {
                    _missingHits++;
                    return true;
                }

                _missingResources.Remove(owned);
            }

            _missingMisses++;
            return false;
        }
    }

    public void MarkMissing(
        DictionaryId dictionaryId,
        DecodedResourceCacheKey key,
        DateTimeOffset? now = null)
    {
        OwnedMissingResourceKey owned = new(dictionaryId, key);
        lock (_missingSync)
        {
            if (!_missingResources.ContainsKey(owned) &&
                _missingResources.Count >= MaximumMissingResources)
            {
                OwnedMissingResourceKey oldest = _missingResources
                    .MinBy(pair => pair.Value)
                    .Key;
                _missingResources.Remove(oldest);
            }

            _missingResources[owned] =
                (now ?? DateTimeOffset.UtcNow).Add(MissingResourceLifetime);
        }
    }

    public void TrimForMemoryPressure()
    {
        _recordBlocks.TrimTo(RecordBlockCapacityBytes / 2);
        _decodedResources.TrimTo(DecodedResourceCapacityBytes / 2);
    }

    public DictionaryMemoryCacheStatistics GetStatistics()
    {
        int missingEntries;
        long missingHits;
        long missingMisses;
        lock (_missingSync)
        {
            missingEntries = _missingResources.Count;
            missingHits = _missingHits;
            missingMisses = _missingMisses;
        }

        return new DictionaryMemoryCacheStatistics(
            Generation,
            _recordBlocks.GetStatistics(),
            _decodedResources.GetStatistics(),
            missingEntries,
            missingHits,
            missingMisses);
    }

    private void RemoveMissing(
        DictionaryId dictionaryId,
        DecodedResourceCacheKey key)
    {
        lock (_missingSync)
        {
            _missingResources.Remove(new OwnedMissingResourceKey(dictionaryId, key));
        }
    }

    private readonly record struct OwnedMissingResourceKey(
        DictionaryId DictionaryId,
        DecodedResourceCacheKey Key);
}

internal readonly record struct DictionarySourceIdentity(
    string NormalizedPath,
    long Length,
    long LastWriteTimeUtcTicks)
{
    public static DictionarySourceIdentity FromPath(string path)
    {
        string normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        FileInfo info = new(normalized);
        return info.Exists
            ? new DictionarySourceIdentity(
                normalized,
                info.Length,
                info.LastWriteTimeUtc.Ticks)
            : new DictionarySourceIdentity(normalized, -1, 0);
    }
}

internal readonly record struct DecodedResourceCacheKey(
    DictionarySourceIdentity Source,
    string NormalizedResourcePath,
    int RewriteVersion,
    string CompatibilityMode)
{
    public static DecodedResourceCacheKey Create(
        string sourcePath,
        string normalizedResourcePath,
        int rewriteVersion,
        string compatibilityMode) =>
        new(
            DictionarySourceIdentity.FromPath(sourcePath),
            normalizedResourcePath.ToUpperInvariant(),
            rewriteVersion,
            compatibilityMode);
}

internal readonly record struct RecordBlockCacheKey(
    DictionarySourceIdentity Source,
    long Offset,
    long Size);

internal sealed record DictionaryMemoryCacheStatistics(
    long Generation,
    SizedCacheStatistics RecordBlocks,
    SizedCacheStatistics DecodedResources,
    int MissingResourceEntries,
    long MissingResourceHits,
    long MissingResourceMisses)
{
    public long CurrentBytes => RecordBlocks.CurrentBytes + DecodedResources.CurrentBytes;
}

internal sealed record SizedCacheStatistics(
    long CapacityBytes,
    long MaximumItemBytes,
    long CurrentBytes,
    int EntryCount,
    long Hits,
    long Misses,
    long Evictions,
    long OversizedRejections,
    long PressureTrims);

internal sealed class FairSizedLruCache<TOwner, TKey, TValue>
    where TOwner : notnull
    where TKey : notnull
{
    private readonly long _capacity;
    private readonly long _maximumItemSize;
    private readonly long _minimumOwnerBytes;
    private readonly Func<TValue, long> _sizeOf;
    private readonly Dictionary<OwnedKey, LinkedListNode<Entry>> _entries = [];
    private readonly Dictionary<TOwner, long> _ownerBytes = [];
    private readonly HashSet<TOwner> _activeOwners = [];
    private readonly LinkedList<Entry> _lru = [];
    private readonly object _sync = new();
    private long _size;
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _oversizedRejections;
    private long _pressureTrims;

    public FairSizedLruCache(
        long capacity,
        long maximumItemSize,
        long minimumOwnerBytes,
        Func<TValue, long> sizeOf)
    {
        _capacity = Math.Max(0, capacity);
        _maximumItemSize = Math.Max(0, maximumItemSize);
        _minimumOwnerBytes = Math.Max(0, minimumOwnerBytes);
        _sizeOf = sizeOf;
    }

    public bool TryGet(TOwner owner, TKey key, out TValue? value)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(new OwnedKey(owner, key), out LinkedListNode<Entry>? node))
            {
                _misses++;
                value = default;
                return false;
            }

            _hits++;
            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Set(TOwner owner, TKey key, TValue value)
    {
        long itemSize = Math.Max(0, _sizeOf(value));
        lock (_sync)
        {
            if (_capacity == 0 ||
                itemSize > _maximumItemSize ||
                itemSize > _capacity ||
                (_activeOwners.Count > 0 && !_activeOwners.Contains(owner)))
            {
                _oversizedRejections++;
                return;
            }

            OwnedKey ownedKey = new(owner, key);
            if (_entries.Remove(ownedKey, out LinkedListNode<Entry>? existing))
            {
                RemoveNode(existing, eviction: false);
            }

            LinkedListNode<Entry> node = new(new Entry(ownedKey, value, itemSize));
            _lru.AddFirst(node);
            _entries[ownedKey] = node;
            _ownerBytes[owner] = _ownerBytes.GetValueOrDefault(owner) + itemSize;
            _size += itemSize;
            TrimLocked(_capacity, pressure: false);
        }
    }

    public void Reset(IEnumerable<TOwner> activeOwners)
    {
        lock (_sync)
        {
            _entries.Clear();
            _ownerBytes.Clear();
            _lru.Clear();
            _activeOwners.Clear();
            _activeOwners.UnionWith(activeOwners);
            _size = 0;
        }
    }

    public void TrimTo(long targetBytes)
    {
        lock (_sync)
        {
            TrimLocked(Math.Clamp(targetBytes, 0, _capacity), pressure: true);
        }
    }

    public SizedCacheStatistics GetStatistics()
    {
        lock (_sync)
        {
            return new SizedCacheStatistics(
                _capacity,
                _maximumItemSize,
                _size,
                _entries.Count,
                _hits,
                _misses,
                _evictions,
                _oversizedRejections,
                _pressureTrims);
        }
    }

    private void TrimLocked(long targetBytes, bool pressure)
    {
        while (_size > targetBytes && _lru.Last is not null)
        {
            LinkedListNode<Entry>? candidate = _lru.Last;
            for (LinkedListNode<Entry>? current = _lru.Last;
                 current is not null;
                 current = current.Previous)
            {
                if (_ownerBytes.GetValueOrDefault(current.Value.Key.Owner) >
                    _minimumOwnerBytes)
                {
                    candidate = current;
                    break;
                }
            }

            RemoveNode(candidate, eviction: true);
            if (pressure)
            {
                _pressureTrims++;
            }
        }
    }

    private void RemoveNode(LinkedListNode<Entry> node, bool eviction)
    {
        _lru.Remove(node);
        _entries.Remove(node.Value.Key);
        _size -= node.Value.Size;
        TOwner owner = node.Value.Key.Owner;
        long remaining = _ownerBytes.GetValueOrDefault(owner) - node.Value.Size;
        if (remaining <= 0)
        {
            _ownerBytes.Remove(owner);
        }
        else
        {
            _ownerBytes[owner] = remaining;
        }

        if (eviction)
        {
            _evictions++;
        }
    }

    private readonly record struct OwnedKey(TOwner Owner, TKey Key);

    private sealed record Entry(OwnedKey Key, TValue Value, long Size);
}
