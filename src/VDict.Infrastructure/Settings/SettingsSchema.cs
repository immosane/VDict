using System.Collections.Immutable;
using VDict.Core.Models;
using VDict.Core.Settings;

namespace VDict.Infrastructure.Settings;

public static class SettingsSchema
{
    public const int CurrentVersion = 8;

    public static ApplicationSettings ToModel(SettingsDocument document)
    {
        HashSet<string> seenIds = new(StringComparer.Ordinal);
        IEnumerable<DictionaryDefinition> dictionaries = (document.Dictionaries ?? [])
            .Where(item => item is not null)
            .Select(item =>
            {
                string idValue = DictionaryId.TryParse(item.Id, out DictionaryId parsed) &&
                                 seenIds.Add(parsed.Value)
                    ? parsed.Value
                    : CreateUniqueId(seenIds);
                DictionaryAppearance appearance = new(
                    item.FontSize,
                    item.LineHeight,
                    item.LatinFontFamily ?? string.Empty,
                    item.CjkFontFamily ?? string.Empty,
                    item.OverrideFont,
                    ParseInteractionMode(item.InteractionMode),
                    ParseDarkModeStrategy(item.DarkModeStrategy))
                {
                    OverrideTypography = item.OverrideTypography
                };
                return new DictionaryDefinition(
                    new DictionaryId(idValue),
                    item.Name ?? string.Empty,
                    item.DisplayName,
                    item.MdxPath ?? string.Empty,
                    item.Enabled,
                    item.BlockNetwork,
                    (item.MddPaths ?? []).ToImmutableArray(),
                    (item.CssPaths ?? []).ToImmutableArray(),
                    appearance)
                {
                    LocalScriptMode = ParseLocalScriptMode(item.LocalScriptMode),
                    ScriptPaths = (item.ScriptPaths ?? []).ToImmutableArray()
                };
            });

        StyleDocument style = document.Style ?? new StyleDocument();
        ApplicationAppearance appearance = new(
            style.FontSize,
            style.LineHeight,
            style.ContentZoom,
            style.ContentWidthPercent,
            ParseTheme(style.Theme),
            style.AppFontFamily ?? string.Empty,
            style.LatinFontFamily ?? string.Empty,
            style.CjkFontFamily ?? string.Empty,
            style.CandidatePaneWidth);

        ApplicationSettings settings = new(
            document.Revision,
            dictionaries.ToImmutableArray(),
            appearance,
            document.ReduceDictionaryBrightnessInDarkMode,
            document.ExternalLinkPolicy?.Equals("system-browser", StringComparison.OrdinalIgnoreCase) == true
                ? ExternalLinkPolicy.SystemBrowser
                : ExternalLinkPolicy.Block,
            document.CompatibilityDiagnosticsEnabled)
        {
            UiLanguage = ParseLanguage(document.UiLanguage),
            DetailedDiagnosticsEnabled =
                document.CompatibilityDiagnosticsEnabled &&
                document.CompatibilityDiagnosticsDetailed
        };
        return ApplicationSettingsNormalizer.Normalize(settings);
    }

    public static SettingsDocument FromModel(ApplicationSettings settings)
    {
        return new SettingsDocument
        {
            SchemaVersion = CurrentVersion,
            Revision = settings.Revision,
            UiLanguage = Format(settings.UiLanguage),
            Dictionaries = settings.Dictionaries.Select(dictionary => new DictionaryDocument
            {
                Id = dictionary.Id.Value,
                Name = dictionary.Name,
                DisplayName = dictionary.DisplayName,
                MdxPath = dictionary.MdxPath,
                Enabled = dictionary.Enabled,
                BlockNetwork = dictionary.BlockNetwork,
                LocalScriptMode = Format(dictionary.LocalScriptMode),
                MddPaths = dictionary.MddPaths.ToList(),
                CssPaths = dictionary.CssPaths.ToList(),
                ScriptPaths = dictionary.ScriptPaths.ToList(),
                FontSize = dictionary.Appearance.FontSize,
                LineHeight = dictionary.Appearance.LineHeight,
                LatinFontFamily = dictionary.Appearance.LatinFontFamily,
                CjkFontFamily = dictionary.Appearance.CjkFontFamily,
                OverrideFont = dictionary.Appearance.OverrideFont,
                OverrideTypography = dictionary.Appearance.OverrideTypography,
                InteractionMode = Format(dictionary.Appearance.InteractionMode),
                DarkModeStrategy = Format(dictionary.Appearance.DarkModeStrategy)
            }).ToList(),
            Style = new StyleDocument
            {
                FontSize = settings.Appearance.FontSize,
                LineHeight = settings.Appearance.LineHeight,
                ContentZoom = settings.Appearance.ContentZoom,
                ContentWidthPercent = settings.Appearance.ContentWidthPercent,
                CandidatePaneWidth = settings.Appearance.CandidatePaneWidth,
                Theme = Format(settings.Appearance.Theme),
                AppFontFamily = settings.Appearance.AppFontFamily,
                LatinFontFamily = settings.Appearance.LatinFontFamily,
                CjkFontFamily = settings.Appearance.CjkFontFamily
            },
            ReduceDictionaryBrightnessInDarkMode = settings.ReduceDictionaryBrightnessInDarkMode,
            ExternalLinkPolicy = settings.ExternalLinkPolicy == ExternalLinkPolicy.SystemBrowser
                ? "system-browser"
                : "block",
            CompatibilityDiagnosticsEnabled = settings.CompatibilityDiagnosticsEnabled,
            CompatibilityDiagnosticsDetailed =
                settings.CompatibilityDiagnosticsEnabled &&
                settings.DetailedDiagnosticsEnabled
        };
    }

    public static SettingsMigrationResult Migrate(SettingsDocument document)
    {
        int sourceVersion = document.SchemaVersion;
        List<string> steps = [];

        if (sourceVersion < 0 || sourceVersion > CurrentVersion)
        {
            throw new InvalidDataException($"不支持设置 schema 版本 {sourceVersion}。");
        }

        if (sourceVersion == 0)
        {
            document.SchemaVersion = 1;
            document.Revision = Math.Max(0, document.Revision);
            steps.Add("schema-v0-to-v1");
        }

        if (document.SchemaVersion == 1)
        {
            foreach (DictionaryDocument dictionary in document.Dictionaries ?? [])
            {
                dictionary.LocalScriptMode = "standard";
            }

            document.SchemaVersion = 2;
            steps.Add("v1-local-script-mode-standard");
        }

        if (document.SchemaVersion == 2)
        {
            document.Style ??= new StyleDocument();
            if (document.Style.ContentWidthPercent <= 0)
            {
                document.Style.ContentWidthPercent =
                    ApplicationAppearance.Default.ContentWidthPercent;
            }

            document.SchemaVersion = 3;
            steps.Add("v2-content-width-percent-default-80");
        }

        if (document.SchemaVersion == 3)
        {
            foreach (DictionaryDocument dictionary in document.Dictionaries ?? [])
            {
                dictionary.ScriptPaths ??= [];
                dictionary.OverrideTypography = false;
            }

            document.SchemaVersion = 4;
            steps.Add("v3-native-dictionary-rendering-defaults");
        }

        if (document.SchemaVersion == 4)
        {
            // v4 files may contain an explicit user choice of "original", or an
            // indistinguishable value written by the old v3 migration. Preserve
            // both. v3 "global" values reach this step unchanged.
            document.SchemaVersion = 5;
            steps.Add("v4-preserve-dictionary-theme-intent");
        }

        if (document.SchemaVersion == 5)
        {
            document.Style ??= new StyleDocument();
            document.Style.CandidatePaneWidth = null;
            document.SchemaVersion = 6;
            steps.Add("v5-candidate-pane-width-unset");
        }

        if (document.SchemaVersion == 6)
        {
            document.CompatibilityDiagnosticsDetailed = false;
            document.SchemaVersion = 7;
            steps.Add("v6-diagnostics-level-standard");
        }

        if (document.SchemaVersion == 7)
        {
            document.UiLanguage = "system";
            document.SchemaVersion = 8;
            steps.Add("v7-ui-language-system");
        }

        return new SettingsMigrationResult(
            sourceVersion,
            document.SchemaVersion,
            steps.ToImmutableArray(),
            ToModel(document));
    }

    private static DictionaryInteractionMode ParseInteractionMode(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "native" => DictionaryInteractionMode.Native,
            "fallback" => DictionaryInteractionMode.Fallback,
            _ => DictionaryInteractionMode.Auto
        };

    private static DictionaryDarkModeStrategy ParseDarkModeStrategy(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "global" => DictionaryDarkModeStrategy.Global,
            "light" or "original" => DictionaryDarkModeStrategy.Light,
            "dark" or "transform" => DictionaryDarkModeStrategy.Dark,
            _ => DictionaryDarkModeStrategy.Light
        };

    private static LocalScriptMode ParseLocalScriptMode(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "legacy" => LocalScriptMode.Legacy,
            "strict" => LocalScriptMode.Strict,
            _ => LocalScriptMode.Standard
        };

    private static ApplicationTheme ParseTheme(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "light" => ApplicationTheme.Light,
            "dark" => ApplicationTheme.Dark,
            _ => ApplicationTheme.System
        };

    public static ApplicationLanguage ParseLanguage(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "zh-cn" => ApplicationLanguage.ChineseSimplified,
            "en-us" => ApplicationLanguage.English,
            _ => ApplicationLanguage.System
        };

    private static string Format(DictionaryInteractionMode value) => value switch
    {
        DictionaryInteractionMode.Native => "native",
        DictionaryInteractionMode.Fallback => "fallback",
        _ => "auto"
    };

    private static string Format(DictionaryDarkModeStrategy value) => value switch
    {
        DictionaryDarkModeStrategy.Light => "original",
        DictionaryDarkModeStrategy.Dark => "dark",
        _ => "global"
    };

    private static string Format(LocalScriptMode value) => value switch
    {
        LocalScriptMode.Legacy => "legacy",
        LocalScriptMode.Strict => "strict",
        _ => "standard"
    };

    private static string Format(ApplicationTheme value) => value switch
    {
        ApplicationTheme.Light => "light",
        ApplicationTheme.Dark => "dark",
        _ => "system"
    };

    public static string Format(ApplicationLanguage value) => value switch
    {
        ApplicationLanguage.ChineseSimplified => "zh-CN",
        ApplicationLanguage.English => "en-US",
        _ => "system"
    };

    private static string CreateUniqueId(HashSet<string> seenIds)
    {
        string value;
        do
        {
            value = Guid.NewGuid().ToString("N");
        }
        while (!seenIds.Add(value));

        return value;
    }
}

public sealed record SettingsMigrationResult(
    int SourceVersion,
    int TargetVersion,
    ImmutableArray<string> Steps,
    ApplicationSettings Settings);
