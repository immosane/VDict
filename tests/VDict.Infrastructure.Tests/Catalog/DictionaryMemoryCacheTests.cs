using VDict.Core.Models;
using VDict.Infrastructure.Catalog;

namespace VDict.Infrastructure.Tests.Catalog;

public sealed class DictionaryMemoryCacheTests
{
    [Fact]
    public void FairCache_ProtectsAnotherActiveOwnersMinimumShare()
    {
        FairSizedLruCache<string, string, byte[]> cache = new(
            capacity: 100,
            maximumItemSize: 100,
            minimumOwnerBytes: 20,
            value => value.LongLength);
        cache.Reset(["large", "small"]);
        cache.Set("small", "kept", new byte[20]);
        cache.Set("large", "one", new byte[40]);
        cache.Set("large", "two", new byte[40]);
        cache.Set("large", "three", new byte[40]);

        SizedCacheStatistics statistics = cache.GetStatistics();

        Assert.InRange(statistics.CurrentBytes, 0, 100);
        Assert.True(cache.TryGet("small", "kept", out byte[]? kept));
        Assert.Equal(20, kept!.Length);
        Assert.True(statistics.Evictions > 0);
    }

    [Fact]
    public void FairCache_RejectsOversizedItemsAndCanTrimForPressure()
    {
        FairSizedLruCache<string, string, byte[]> cache = new(
            capacity: 100,
            maximumItemSize: 60,
            minimumOwnerBytes: 10,
            value => value.LongLength);
        cache.Reset(["one"]);
        cache.Set("one", "oversized", new byte[61]);
        cache.Set("one", "first", new byte[40]);
        cache.Set("one", "second", new byte[40]);

        cache.TrimTo(40);
        SizedCacheStatistics statistics = cache.GetStatistics();

        Assert.Equal(40, statistics.CurrentBytes);
        Assert.Equal(1, statistics.OversizedRejections);
        Assert.Equal(1, statistics.PressureTrims);
    }

    [Fact]
    public void SourceIdentity_ChangesWhenTheSourceFileChanges()
    {
        using TestDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "source.mdd");
        File.WriteAllBytes(path, [1, 2, 3]);
        DictionarySourceIdentity first = DictionarySourceIdentity.FromPath(path);

        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        DictionarySourceIdentity second = DictionarySourceIdentity.FromPath(path);

        Assert.NotEqual(first, second);
        Assert.Equal(Path.GetFullPath(path).ToUpperInvariant(), second.NormalizedPath);
    }

    [Fact]
    public void GlobalCache_UsesBoundedCategoryBudgetsAndGenerationInvalidation()
    {
        DictionaryId first = new("first");
        DictionaryId second = new("second");
        DictionaryMemoryCache cache = new();
        cache.AdvanceGeneration(7, [first, second]);
        RecordBlockCacheKey recordKey = new(
            new DictionarySourceIdentity("C:\\FIRST.MDD", 100, 1),
            10,
            20);
        DecodedResourceCacheKey resourceKey = new(
            new DictionarySourceIdentity("C:\\SECOND.MDD", 200, 2),
            "IMAGE.PNG",
            1,
            "Standard:True");
        cache.StoreRecordBlock(first, recordKey, new byte[128]);
        cache.StoreDecodedResource(
            second,
            resourceKey,
            new ResourceContent("image/png", new byte[64]));
        cache.MarkMissing(second, resourceKey with { NormalizedResourcePath = "MISSING.PNG" });

        DictionaryMemoryCacheStatistics populated = cache.GetStatistics();
        Assert.Equal(192, populated.CurrentBytes);
        Assert.Equal(DictionaryMemoryCache.TotalCapacityBytes,
            populated.RecordBlocks.CapacityBytes + populated.DecodedResources.CapacityBytes);
        Assert.Equal(1, populated.MissingResourceEntries);

        cache.AdvanceGeneration(8, [second]);
        DictionaryMemoryCacheStatistics invalidated = cache.GetStatistics();

        Assert.Equal(8, invalidated.Generation);
        Assert.Equal(0, invalidated.CurrentBytes);
        Assert.Equal(0, invalidated.MissingResourceEntries);
        Assert.False(cache.TryGetRecordBlock(first, recordKey, out _));
    }

    [Fact]
    public void MissingResourceCache_IsShortLivedAndDoesNotCacheFailuresForever()
    {
        DictionaryId dictionaryId = new("dictionary");
        DictionaryMemoryCache cache = new();
        cache.AdvanceGeneration(1, [dictionaryId]);
        DecodedResourceCacheKey key = new(
            new DictionarySourceIdentity("C:\\DICT.MDD", 10, 1),
            "MISSING.PNG",
            1,
            "Standard:True");
        DateTimeOffset observed = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);

        cache.MarkMissing(dictionaryId, key, observed);

        Assert.True(cache.IsKnownMissing(dictionaryId, key, observed.AddSeconds(29)));
        Assert.False(cache.IsKnownMissing(dictionaryId, key, observed.AddSeconds(31)));
    }

    [Fact]
    public void MissingResourceCache_RemainsBounded()
    {
        DictionaryId dictionaryId = new("dictionary");
        DictionaryMemoryCache cache = new();
        cache.AdvanceGeneration(1, [dictionaryId]);
        DictionarySourceIdentity source = new("C:\\DICT.MDD", 10, 1);
        for (int index = 0; index < 1500; index++)
        {
            cache.MarkMissing(
                dictionaryId,
                new DecodedResourceCacheKey(
                    source,
                    $"MISSING-{index}.PNG",
                    1,
                    "Standard:True"));
        }

        Assert.Equal(1024, cache.GetStatistics().MissingResourceEntries);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "VDict.CacheTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
