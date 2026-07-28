using VDict.Infrastructure.DictionaryFormats;

namespace VDict.Infrastructure.Tests.DictionaryFormats;

public sealed class MddRoutingTests
{
    [Fact]
    public void Heuristics_PreferNumberedAudioAndImagePackages()
    {
        Assert.True(
            MddRouting.Priority(
                "dictionary.1.mdd",
                DictionaryResourceKind.Audio,
                null) <
            MddRouting.Priority(
                "dictionary.2.mdd",
                DictionaryResourceKind.Audio,
                null));
        Assert.True(
            MddRouting.Priority(
                "dictionary.2.mdd",
                DictionaryResourceKind.Image,
                null) <
            MddRouting.Priority(
                "dictionary.1.mdd",
                DictionaryResourceKind.Image,
                null));
    }

    [Fact]
    public void Manifest_PersistsProfilesAndInvalidatesChangedFiles()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "VDict.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string mdd = Path.Combine(directory, "dictionary.mdd");
            string cache = Path.Combine(directory, "dictionary.index");
            File.WriteAllBytes(mdd, [1, 2, 3]);
            MddResourceProfile profile = new();
            profile.Observe("audio/word.mp3");

            new MddProfileManifest(cache).Update(mdd, profile);
            MddResourceProfile? restored =
                new MddProfileManifest(cache).ProfileFor(mdd);

            Assert.NotNull(restored);
            Assert.Equal(1, restored.AudioCount);

            File.AppendAllText(mdd, "changed");

            Assert.Null(new MddProfileManifest(cache).ProfileFor(mdd));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
