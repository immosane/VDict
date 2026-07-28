using System.Collections.Immutable;
using VDict.Core.Models;
using VDict.Core.Settings;

namespace VDict.Core.Tests.Settings;

public sealed class SettingsChangeClassifierTests
{
    [Fact]
    public void UiLanguage_IsIndependentAndDoesNotReloadCatalog()
    {
        ApplicationSettings previous = WithDictionary();
        ApplicationSettings current = previous with
        {
            UiLanguage = ApplicationLanguage.ChineseSimplified
        };

        SettingsChangeSet changes = SettingsChangeClassifier.Classify(previous, current);

        Assert.True(changes.Has(SettingsChangeKind.UiLanguage));
        Assert.False(changes.RequiresCatalogReload);
    }

    [Fact]
    public void Zoom_IsReaderOnlyAndDoesNotRequireCatalogReload()
    {
        ApplicationSettings previous = WithDictionary();
        ApplicationSettings current = previous with
        {
            Appearance = previous.Appearance with { ContentZoom = 1.4 }
        };

        SettingsChangeSet changes = SettingsChangeClassifier.Classify(previous, current);

        Assert.True(changes.Has(SettingsChangeKind.Zoom));
        Assert.False(changes.RequiresCatalogReload);
    }

    [Fact]
    public void DictionaryResourceAndSecurityChangesRequireCatalogReload()
    {
        ApplicationSettings previous = WithDictionary();
        DictionaryDefinition changed = previous.Dictionaries[0] with
        {
            BlockNetwork = false,
            MddPaths = ["replacement.mdd"]
        };
        ApplicationSettings current = previous with { Dictionaries = [changed] };

        SettingsChangeSet changes = SettingsChangeClassifier.Classify(previous, current);

        Assert.True(changes.Has(SettingsChangeKind.CatalogStructure));
        Assert.True(changes.Has(SettingsChangeKind.SecurityPolicy));
        Assert.True(changes.RequiresCatalogReload);
    }

    [Fact]
    public void CandidatePaneWidth_IsLayoutOnlyAndDoesNotRequireCatalogReload()
    {
        ApplicationSettings previous = WithDictionary();
        ApplicationSettings current = previous with
        {
            Appearance = previous.Appearance with { CandidatePaneWidth = 312 }
        };

        SettingsChangeSet changes = SettingsChangeClassifier.Classify(previous, current);

        Assert.True(changes.Has(SettingsChangeKind.MainWindowLayout));
        Assert.False(changes.Has(SettingsChangeKind.ReaderAppearance));
        Assert.False(changes.RequiresCatalogReload);
    }

    [Fact]
    public void ThemeTypographyAndDiagnosticsAreClassifiedIndependently()
    {
        ApplicationSettings previous = WithDictionary();
        ApplicationSettings current = previous with
        {
            Appearance = previous.Appearance with
            {
                Theme = ApplicationTheme.Dark,
                FontSize = 20
            },
            CompatibilityDiagnosticsEnabled = true,
            DetailedDiagnosticsEnabled = true
        };

        SettingsChangeSet changes = SettingsChangeClassifier.Classify(previous, current);

        Assert.True(changes.Has(SettingsChangeKind.ApplicationTheme));
        Assert.True(changes.Has(SettingsChangeKind.ReaderAppearance));
        Assert.True(changes.Has(SettingsChangeKind.Diagnostics));
        Assert.False(changes.RequiresCatalogReload);
    }

    private static ApplicationSettings WithDictionary() =>
        ApplicationSettings.Default with
        {
            Dictionaries =
            [
                new DictionaryDefinition(
                    new DictionaryId("dict"),
                    "Dictionary",
                    null,
                    "dictionary.mdx",
                    true,
                    true,
                    ImmutableArray<string>.Empty,
                    ImmutableArray<string>.Empty,
                    DictionaryAppearance.Default)
            ]
        };
}
