using VDict.Core.Models;
using VDict.Presentation.Settings;
using VDict.Presentation.Tests.TestDoubles;

namespace VDict.Presentation.Tests.Settings;

public sealed class SettingsSectionCacheTests
{
    [Fact]
    public void GetOrCreate_CreatesEachSectionOnceAndRetainsItsState()
    {
        int created = 0;
        SettingsSectionCache<TestSection> cache = new(
            key => new TestSection(key, ++created));

        TestSection dictionaries = cache.GetOrCreate("dictionaries");
        dictionaries.DraftText = "未保存草稿";
        TestSection reading = cache.GetOrCreate("reading");
        TestSection dictionariesAgain = cache.GetOrCreate("dictionaries");

        Assert.Same(dictionaries, dictionariesAgain);
        Assert.Equal("未保存草稿", dictionariesAgain.DraftText);
        Assert.Equal(2, created);
        Assert.Equal(2, cache.Count);
        Assert.Equal("reading", reading.Key);
    }

    [Fact]
    public void Dispose_ReleasesEveryCreatedSectionExactlyOnce()
    {
        Dictionary<string, int> releases = new(StringComparer.Ordinal);
        SettingsSectionCache<TestSection> cache = new(
            key => new TestSection(key, 0),
            section => releases[section.Key] =
                releases.GetValueOrDefault(section.Key) + 1);
        _ = cache.GetOrCreate("dictionaries");
        _ = cache.GetOrCreate("reading");
        _ = cache.GetOrCreate("security");

        cache.Dispose();
        cache.Dispose();

        Assert.Equal(3, releases.Count);
        Assert.All(releases.Values, count => Assert.Equal(1, count));
        Assert.Equal(0, cache.Count);
        Assert.Throws<ObjectDisposedException>(
            () => cache.GetOrCreate("about"));
    }

    [Fact]
    public void Navigation_RetainsSharedDraftValidationAndSectionSelection()
    {
        DictionaryDefinition dictionary = new(
            new DictionaryId("phase4"),
            "phase4",
            null,
            "C:\\phase4.mdx",
            true,
            true,
            [],
            [],
            DictionaryAppearance.Default);
        SettingsEditSession session = new(
            ApplicationSettings.Default with { Dictionaries = [dictionary] },
            new FakeCatalog());
        SettingsSectionCache<SessionBackedSection> cache = new(
            key => new SessionBackedSection(key, session));

        SessionBackedSection dictionaries = cache.GetOrCreate("dictionaries");
        dictionaries.SelectedDictionaryId = dictionary.Id;
        session.Update(
            settings => settings with
            {
                Appearance = settings.Appearance with { FontSize = 60 }
            },
            SettingsDirtySection.Reader);
        SessionBackedSection reading = cache.GetOrCreate("reading");
        SessionBackedSection dictionariesAgain = cache.GetOrCreate("dictionaries");

        Assert.Same(session, dictionaries.Session);
        Assert.Same(session, reading.Session);
        Assert.Same(dictionaries, dictionariesAgain);
        Assert.Equal(dictionary.Id, dictionariesAgain.SelectedDictionaryId);
        Assert.Equal(60, dictionariesAgain.Session.Draft.Appearance.FontSize);
        Assert.False(dictionariesAgain.Session.Validation.IsValid);
    }

    private sealed class TestSection(string key, int constructionNumber)
    {
        public string Key { get; } = key;
        public int ConstructionNumber { get; } = constructionNumber;
        public string? DraftText { get; set; }
    }

    private sealed class SessionBackedSection(
        string key,
        SettingsEditSession session)
    {
        public string Key { get; } = key;
        public SettingsEditSession Session { get; } = session;
        public DictionaryId? SelectedDictionaryId { get; set; }
    }
}
