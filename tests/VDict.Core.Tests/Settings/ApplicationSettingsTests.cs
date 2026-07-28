using System.Collections.Immutable;
using VDict.Core.Models;
using VDict.Core.Settings;

namespace VDict.Core.Tests.Settings;

public sealed class ApplicationSettingsTests
{
    [Fact]
    public void Language_DefaultsToSystemAndInvalidValuesNormalizeToSystem()
    {
        Assert.Equal(ApplicationLanguage.System, ApplicationSettings.Default.UiLanguage);

        ApplicationSettings normalized = ApplicationSettingsNormalizer.Normalize(
            ApplicationSettings.Default with
            {
                UiLanguage = (ApplicationLanguage)999
            });

        Assert.Equal(ApplicationLanguage.System, normalized.UiLanguage);
    }

    [Fact]
    public void Normalize_ClampsValuesAndRemovesDuplicatePaths()
    {
        DictionaryDefinition dictionary = CreateDictionary() with
        {
            Name = " ",
            DisplayName = "  Display  ",
            MdxPath = " C:\\dict\\sample.mdx ",
            MddPaths = ["a.mdd", "A.mdd", " "],
            ScriptPaths = ["a.js", "A.js", " "],
            Appearance = DictionaryAppearance.Default with { FontSize = 100, LineHeight = 0 }
        };
        ApplicationSettings source = ApplicationSettings.Default with
        {
            Revision = -5,
            Dictionaries = [dictionary],
            Appearance = ApplicationAppearance.Default with
            {
                ContentZoom = 10,
                ContentWidthPercent = 500,
                CandidatePaneWidth = 900
            }
        };

        ApplicationSettings normalized = ApplicationSettingsNormalizer.Normalize(source);

        Assert.Equal(0, normalized.Revision);
        Assert.Equal("sample", normalized.Dictionaries[0].Name);
        Assert.Equal("Display", normalized.Dictionaries[0].DisplayName);
        Assert.Equal(48, normalized.Dictionaries[0].Appearance.FontSize);
        Assert.Equal(1.65, normalized.Dictionaries[0].Appearance.LineHeight);
        Assert.Single(normalized.Dictionaries[0].MddPaths);
        Assert.Single(normalized.Dictionaries[0].ScriptPaths);
        Assert.Equal(2.5, normalized.Appearance.ContentZoom);
        Assert.Equal(100, normalized.Appearance.ContentWidthPercent);
        Assert.Equal(380, normalized.Appearance.CandidatePaneWidth);
    }

    [Fact]
    public void StructuralComparer_UsesCollectionContentsAndOrder()
    {
        DictionaryDefinition dictionary = CreateDictionary();
        ApplicationSettings left = ApplicationSettings.Default with { Dictionaries = [dictionary] };
        ApplicationSettings same = ApplicationSettings.Default with
        {
            Dictionaries = ImmutableArray.Create(dictionary with
            {
                MddPaths = ImmutableArray.Create(dictionary.MddPaths.ToArray())
            })
        };
        ApplicationSettings reordered = left with
        {
            Dictionaries =
            [
                dictionary with { Id = new DictionaryId("other") },
                dictionary
            ]
        };

        Assert.True(ApplicationSettingsComparer.ContentEquals(left, same));
        Assert.False(ApplicationSettingsComparer.ContentEquals(left, reordered));
    }

    [Fact]
    public void StructuralComparer_IncludesUiLanguage()
    {
        ApplicationSettings english = ApplicationSettings.Default with
        {
            UiLanguage = ApplicationLanguage.English
        };

        Assert.False(
            ApplicationSettingsComparer.ContentEquals(
                ApplicationSettings.Default,
                english));
    }

    [Fact]
    public void Validation_ReportsRangeAndMissingMdxPerDictionary()
    {
        ApplicationSettings settings = ApplicationSettings.Default with
        {
            Appearance = ApplicationAppearance.Default with { ContentWidthPercent = 20 },
            Dictionaries =
            [
                CreateDictionary() with
                {
                    MdxPath = string.Empty,
                    Appearance = DictionaryAppearance.Default with { FontSize = 9 }
                }
            ]
        };

        SettingsValidationResult result = ApplicationSettingsNormalizer.Validate(settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "required");
        Assert.Contains(result.Errors, error => error.Code == "range");
        Assert.Contains(
            result.Errors,
            error => error.Path == "appearance.content_width_percent");
    }

    [Fact]
    public void ContentWidth_DefaultsToEightyPercent()
    {
        Assert.Equal(80, ApplicationAppearance.Default.ContentWidthPercent);
    }

    [Fact]
    public void CandidatePaneWidth_DefaultsToUnset()
    {
        Assert.Null(ApplicationAppearance.Default.CandidatePaneWidth);
    }

    [Fact]
    public void Normalize_DisablesDetailedDiagnosticsWhenDiagnosticsAreOff()
    {
        ApplicationSettings settings = ApplicationSettings.Default with
        {
            DetailedDiagnosticsEnabled = true
        };

        ApplicationSettings normalized = ApplicationSettingsNormalizer.Normalize(settings);

        Assert.Equal(CompatibilityDiagnosticsLevel.Off, normalized.DiagnosticsLevel);
        Assert.False(normalized.DetailedDiagnosticsEnabled);
    }

    [Fact]
    public void NewDictionaryDefault_FollowsApplicationWithoutChangingSafeFallback()
    {
        Assert.Equal(
            DictionaryDarkModeStrategy.Light,
            DictionaryAppearance.Default.DarkModeStrategy);
        Assert.Equal(
            DictionaryDarkModeStrategy.Global,
            DictionaryAppearance.NewDictionaryDefault.DarkModeStrategy);
    }

    [Fact]
    public void Validation_RejectsMissingOrNonJavaScriptManualScriptPaths()
    {
        DictionaryDefinition dictionary = CreateDictionary() with
        {
            ScriptPaths = [@"Z:\missing\dictionary.js", @"Z:\missing\dictionary.txt"]
        };

        SettingsValidationResult result = ApplicationSettingsNormalizer.Validate(
            ApplicationSettings.Default with { Dictionaries = [dictionary] });

        Assert.Equal(2, result.Errors.Count(error => error.Code == "script_file"));
    }

    [Fact]
    public void Normalize_DoesNotHideDuplicateDictionaryIds()
    {
        DictionaryDefinition first = CreateDictionary();
        ApplicationSettings settings = ApplicationSettings.Default with
        {
            Dictionaries =
            [
                first,
                first with { Name = "Second", MdxPath = @"C:\dict\second.mdx" }
            ]
        };

        ApplicationSettings normalized = ApplicationSettingsNormalizer.Normalize(settings);
        SettingsValidationResult validation = ApplicationSettingsNormalizer.Validate(normalized);

        Assert.Equal(first.Id, normalized.Dictionaries[1].Id);
        Assert.Contains(validation.Errors, error => error.Code == "dictionary_id");
    }

    private static DictionaryDefinition CreateDictionary() => new(
        new DictionaryId("dict"),
        "Sample",
        null,
        "C:\\dict\\sample.mdx",
        true,
        true,
        ["sample.mdd"],
        [],
        DictionaryAppearance.Default);
}
