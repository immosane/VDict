using VDict.Core.Models;
using VDict.Core.Policies;
using VDict.Infrastructure.Catalog;

namespace VDict.Infrastructure.DictionaryFormats;

internal sealed class DictionaryResourceCache
{
    private readonly DictionaryMemoryCache _cache;
    private readonly DictionaryId _dictionaryId;

    public DictionaryResourceCache()
    {
        _dictionaryId = new DictionaryId("standalone");
        _cache = new DictionaryMemoryCache();
        _cache.AdvanceGeneration(1, [_dictionaryId]);
    }

    public DictionaryResourceCache(
        DictionaryMemoryCache cache,
        DictionaryId dictionaryId)
    {
        _cache = cache;
        _dictionaryId = dictionaryId;
    }

    public DictionarySourceIdentity GetRecordSourceIdentity(string path) =>
        DictionarySourceIdentity.FromPath(path);

    public bool TryGetRecordBlock(RecordBlockCacheKey key, out byte[]? block) =>
        _cache.TryGetRecordBlock(_dictionaryId, key, out block);

    public void StoreRecordBlock(RecordBlockCacheKey key, byte[] block) =>
        _cache.StoreRecordBlock(_dictionaryId, key, block);
}

internal static class DictionaryStore
{
    public static string NormalizeResourcePath(string path) =>
        ResourcePathPolicy.TryNormalize(path, out string normalized)
            ? normalized
            : string.Empty;
}
