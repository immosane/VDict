using System.Collections.Immutable;
using VDict.Core.Models;
using VDict.Core.Settings;

namespace VDict.Core.Tests.Settings;

public sealed class SettingsThreeWayMergerTests
{
    [Fact]
    public void Merge_PreservesIndependentLanguageChange()
    {
        ApplicationSettings @base = ApplicationSettings.Default;
        ApplicationSettings current = @base with
        {
            Revision = 2,
            UiLanguage = ApplicationLanguage.English
        };
        ApplicationSettings draft = @base with
        {
            CompatibilityDiagnosticsEnabled = true
        };

        SettingsMergeResult result = SettingsThreeWayMerger.Merge(@base, current, draft);

        Assert.True(result.Succeeded);
        Assert.Equal(ApplicationLanguage.English, result.Settings!.UiLanguage);
        Assert.True(result.Settings.CompatibilityDiagnosticsEnabled);
    }

    [Fact]
    public void Merge_ReportsConflictingLanguageChanges()
    {
        ApplicationSettings @base = ApplicationSettings.Default;
        ApplicationSettings current = @base with
        {
            Revision = 2,
            UiLanguage = ApplicationLanguage.English
        };
        ApplicationSettings draft = @base with
        {
            UiLanguage = ApplicationLanguage.ChineseSimplified
        };

        SettingsMergeResult result = SettingsThreeWayMerger.Merge(@base, current, draft);

        Assert.False(result.Succeeded);
        Assert.Contains("UiLanguage", result.Conflicts);
    }

    [Fact]
    public void Merge_PreservesIndependentCurrentAndDraftFields()
    {
        ApplicationSettings @base = ApplicationSettings.Default;
        ApplicationSettings current = @base with
        {
            Revision = 2,
            Appearance = @base.Appearance with { ContentZoom = 1.4 }
        };
        ApplicationSettings draft = @base with
        {
            CompatibilityDiagnosticsEnabled = true
        };

        SettingsMergeResult result = SettingsThreeWayMerger.Merge(
            @base,
            current,
            draft);

        Assert.True(result.Succeeded);
        Assert.Equal(1.4, result.Settings!.Appearance.ContentZoom, 3);
        Assert.True(result.Settings.CompatibilityDiagnosticsEnabled);
        Assert.Equal(2, result.Settings.Revision);
    }

    [Fact]
    public void Merge_ReportsSameFieldConflict()
    {
        ApplicationSettings @base = ApplicationSettings.Default;
        ApplicationSettings current = @base with
        {
            Revision = 2,
            Appearance = @base.Appearance with { ContentZoom = 1.4 }
        };
        ApplicationSettings draft = @base with
        {
            Appearance = @base.Appearance with { ContentZoom = 0.8 }
        };

        SettingsMergeResult result = SettingsThreeWayMerger.Merge(
            @base,
            current,
            draft);

        Assert.False(result.Succeeded);
        Assert.Contains("Appearance.ContentZoom", result.Conflicts);
    }

    [Fact]
    public void Merge_CombinesDifferentFieldsOfSameDictionaryById()
    {
        DictionaryDefinition dictionary = CreateDictionary("one");
        ApplicationSettings @base = ApplicationSettings.Default with
        {
            Dictionaries = [dictionary]
        };
        ApplicationSettings current = @base with
        {
            Revision = 2,
            Dictionaries = [dictionary with { Enabled = false }]
        };
        ApplicationSettings draft = @base with
        {
            Dictionaries =
            [
                dictionary with
                {
                    DisplayName = "Draft name",
                    Appearance = dictionary.Appearance with { FontSize = 22 }
                }
            ]
        };

        SettingsMergeResult result = SettingsThreeWayMerger.Merge(@base, current, draft);

        Assert.True(result.Succeeded);
        DictionaryDefinition merged = Assert.Single(result.Settings!.Dictionaries);
        Assert.False(merged.Enabled);
        Assert.Equal("Draft name", merged.DisplayName);
        Assert.Equal(22, merged.Appearance.FontSize);
    }

    [Fact]
    public void Merge_ReportsDictionaryFieldConflictWithDictionaryId()
    {
        DictionaryDefinition dictionary = CreateDictionary("one");
        ApplicationSettings @base = ApplicationSettings.Default with
        {
            Dictionaries = [dictionary]
        };
        ApplicationSettings current = @base with
        {
            Revision = 2,
            Dictionaries = [dictionary with { DisplayName = "Current" }]
        };
        ApplicationSettings draft = @base with
        {
            Dictionaries = [dictionary with { DisplayName = "Draft" }]
        };

        SettingsMergeResult result = SettingsThreeWayMerger.Merge(@base, current, draft);

        Assert.False(result.Succeeded);
        Assert.Contains("Dictionaries[one].DisplayName", result.Conflicts);
    }

    [Fact]
    public void Merge_AllowsOneSideToReorderWhileOtherEditsAField()
    {
        DictionaryDefinition one = CreateDictionary("one");
        DictionaryDefinition two = CreateDictionary("two");
        ApplicationSettings @base = ApplicationSettings.Default with
        {
            Dictionaries = [one, two]
        };
        ApplicationSettings current = @base with
        {
            Revision = 2,
            Dictionaries = [two, one]
        };
        ApplicationSettings draft = @base with
        {
            Dictionaries = [one with { Enabled = false }, two]
        };

        SettingsMergeResult result = SettingsThreeWayMerger.Merge(@base, current, draft);

        Assert.True(result.Succeeded);
        Assert.Equal(["two", "one"], result.Settings!.Dictionaries.Select(item => item.Id.Value));
        Assert.False(result.Settings.Dictionaries[1].Enabled);
    }

    [Fact]
    public void Merge_ReportsDeleteVersusModifyConflict()
    {
        DictionaryDefinition dictionary = CreateDictionary("one");
        ApplicationSettings @base = ApplicationSettings.Default with
        {
            Dictionaries = [dictionary]
        };
        ApplicationSettings current = @base with
        {
            Revision = 2,
            Dictionaries = []
        };
        ApplicationSettings draft = @base with
        {
            Dictionaries = [dictionary with { DisplayName = "Draft" }]
        };

        SettingsMergeResult result = SettingsThreeWayMerger.Merge(@base, current, draft);

        Assert.False(result.Succeeded);
        Assert.Contains("Dictionaries[one].DeletedVsModified", result.Conflicts);
    }

    private static DictionaryDefinition CreateDictionary(string id) => new(
        new DictionaryId(id),
        id,
        null,
        $@"C:\dict\{id}.mdx",
        true,
        true,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        DictionaryAppearance.Default);
}
