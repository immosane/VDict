using VDict.Infrastructure.Indexing;

namespace VDict.Infrastructure.Tests.Indexing;

public sealed class DictionaryIndexPathTests
{
    [Fact]
    public void SameFullPath_ProducesStableVersionedCacheLeaf()
    {
        string root = Path.Combine(Path.GetTempPath(), "indexes");
        string mdx = Path.Combine(Path.GetTempPath(), "dict", "sample.mdx");

        Assert.True(DictionaryIndexPath.TryGet(root, mdx, out string first));
        Assert.True(DictionaryIndexPath.TryGet(root, mdx.ToUpperInvariant(), out string second));
        Assert.EndsWith(".sqlite3", first, StringComparison.Ordinal);
        Assert.Equal(first, second, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(root), Path.GetDirectoryName(first));
    }
}
