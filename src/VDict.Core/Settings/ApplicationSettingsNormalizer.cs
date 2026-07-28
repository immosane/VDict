using System.Collections.Immutable;
using VDict.Core.Localization;
using VDict.Core.Models;

namespace VDict.Core.Settings;

public static class ApplicationSettingsNormalizer
{
    public static ApplicationSettings Normalize(ApplicationSettings settings)
    {
        ImmutableArray<DictionaryDefinition>.Builder dictionaries =
            ImmutableArray.CreateBuilder<DictionaryDefinition>(settings.Dictionaries.Length);

        foreach (DictionaryDefinition dictionary in settings.Dictionaries)
        {
            string mdxPath = dictionary.MdxPath.Trim();
            string name = string.IsNullOrWhiteSpace(dictionary.Name)
                ? Path.GetFileNameWithoutExtension(mdxPath)
                : dictionary.Name.Trim();
            string? displayName = string.IsNullOrWhiteSpace(dictionary.DisplayName)
                ? null
                : dictionary.DisplayName.Trim();

            DictionaryAppearance appearance = dictionary.Appearance with
            {
                FontSize = dictionary.Appearance.FontSize <= 0
                    ? DictionaryAppearance.Default.FontSize
                    : Math.Clamp(dictionary.Appearance.FontSize, 10, 48),
                LineHeight = dictionary.Appearance.LineHeight <= 0
                    ? DictionaryAppearance.Default.LineHeight
                    : Math.Clamp(dictionary.Appearance.LineHeight, 1.0, 3.0),
                LatinFontFamily = NormalizeFont(
                    dictionary.Appearance.LatinFontFamily,
                    DictionaryAppearance.Default.LatinFontFamily),
                CjkFontFamily = NormalizeFont(
                    dictionary.Appearance.CjkFontFamily,
                    DictionaryAppearance.Default.CjkFontFamily)
            };

            dictionaries.Add(dictionary with
            {
                Name = name,
                DisplayName = displayName,
                MdxPath = mdxPath,
                MddPaths = NormalizePaths(dictionary.MddPaths),
                CssPaths = NormalizePaths(dictionary.CssPaths),
                ScriptPaths = NormalizePaths(dictionary.ScriptPaths),
                Appearance = appearance
            });
        }

        ApplicationAppearance appAppearance = settings.Appearance with
        {
            FontSize = settings.Appearance.FontSize <= 0
                ? ApplicationAppearance.Default.FontSize
                : Math.Clamp(settings.Appearance.FontSize, 10, 48),
            LineHeight = settings.Appearance.LineHeight <= 0
                ? ApplicationAppearance.Default.LineHeight
                : Math.Clamp(settings.Appearance.LineHeight, 1.0, 3.0),
            ContentZoom = settings.Appearance.ContentZoom <= 0
                ? ApplicationAppearance.Default.ContentZoom
                : Math.Clamp(settings.Appearance.ContentZoom, 0.5, 2.5),
            ContentWidthPercent = settings.Appearance.ContentWidthPercent <= 0
                ? ApplicationAppearance.Default.ContentWidthPercent
                : Math.Clamp(settings.Appearance.ContentWidthPercent, 30, 100),
            CandidatePaneWidth = settings.Appearance.CandidatePaneWidth is { } paneWidth
                ? Math.Round(Math.Clamp(paneWidth, 180, 380), 0)
                : null,
            AppFontFamily = NormalizeFont(
                settings.Appearance.AppFontFamily,
                ApplicationAppearance.Default.AppFontFamily),
            LatinFontFamily = NormalizeFont(
                settings.Appearance.LatinFontFamily,
                ApplicationAppearance.Default.LatinFontFamily),
            CjkFontFamily = NormalizeFont(
                settings.Appearance.CjkFontFamily,
                ApplicationAppearance.Default.CjkFontFamily)
        };

        return settings with
        {
            Revision = Math.Max(0, settings.Revision),
            UiLanguage = Enum.IsDefined(settings.UiLanguage)
                ? settings.UiLanguage
                : ApplicationLanguage.System,
            Dictionaries = dictionaries.MoveToImmutable(),
            Appearance = appAppearance,
            DetailedDiagnosticsEnabled =
                settings.CompatibilityDiagnosticsEnabled &&
                settings.DetailedDiagnosticsEnabled
        };
    }

    public static SettingsValidationResult Validate(
        ApplicationSettings settings,
        IAppLocalizer? localizer = null)
    {
        localizer ??= IdentityAppLocalizer.Instance;
        ImmutableArray<SettingsValidationError>.Builder errors =
            ImmutableArray.CreateBuilder<SettingsValidationError>();
        HashSet<string> ids = new(StringComparer.Ordinal);

        if (settings.Appearance.FontSize is < 10 or > 48)
        {
            errors.Add(new(
                "appearance.font_size",
                "range",
                localizer.Get("Validation_AppFontSize")));
        }

        if (settings.Appearance.LineHeight is < 1.0 or > 3.0)
        {
            errors.Add(new(
                "appearance.line_height",
                "range",
                localizer.Get("Validation_AppLineHeight")));
        }

        if (settings.Appearance.ContentZoom is < 0.5 or > 2.5)
        {
            errors.Add(new(
                "appearance.content_zoom",
                "range",
                localizer.Get("Validation_ContentZoom")));
        }

        if (settings.Appearance.ContentWidthPercent is < 30 or > 100)
        {
            errors.Add(new(
                "appearance.content_width_percent",
                "range",
                localizer.Get("Validation_ContentWidth")));
        }

        if (settings.Appearance.CandidatePaneWidth is < 180 or > 380)
        {
            errors.Add(new(
                "appearance.candidate_pane_width",
                "range",
                localizer.Get("Validation_CandidatePaneWidth")));
        }

        foreach ((DictionaryDefinition dictionary, int index) in
                 settings.Dictionaries.Select((value, index) => (value, index)))
        {
            string prefix = $"dictionaries[{index}]";
            if (!DictionaryId.TryParse(dictionary.Id.Value, out _) || !ids.Add(dictionary.Id.Value))
            {
                errors.Add(new(
                    $"{prefix}.id",
                    "dictionary_id",
                    localizer.Get("Validation_DictionaryId"),
                    dictionary.Id));
            }

            if (string.IsNullOrWhiteSpace(dictionary.MdxPath))
            {
                errors.Add(new(
                    $"{prefix}.mdx_path",
                    "required",
                    localizer.Get("Validation_MdxRequired"),
                    dictionary.Id));
            }
            else
            {
                ValidatePathSyntax(
                    dictionary.MdxPath,
                    $"{prefix}.mdx_path",
                    dictionary.Id,
                    errors,
                    localizer);
            }

            foreach (string resourcePath in dictionary.MddPaths)
            {
                ValidatePathSyntax(
                    resourcePath,
                    $"{prefix}.mdd_paths",
                    dictionary.Id,
                    errors,
                    localizer);
            }

            foreach (string stylePath in dictionary.CssPaths)
            {
                ValidatePathSyntax(
                    stylePath,
                    $"{prefix}.css_paths",
                    dictionary.Id,
                    errors,
                    localizer);
            }

            if (dictionary.Appearance.FontSize is < 10 or > 48)
            {
                errors.Add(new(
                    $"{prefix}.appearance.font_size",
                    "range",
                    localizer.Get("Validation_DictionaryFontSize"),
                    dictionary.Id));
            }

            if (dictionary.Appearance.LineHeight is < 1.0 or > 3.0)
            {
                errors.Add(new(
                    $"{prefix}.appearance.line_height",
                    "range",
                    localizer.Get("Validation_DictionaryLineHeight"),
                    dictionary.Id));
            }

            foreach (string scriptPath in dictionary.ScriptPaths)
            {
                if (!ValidatePathSyntax(
                        scriptPath,
                        $"{prefix}.script_paths",
                        dictionary.Id,
                        errors,
                        localizer))
                {
                    continue;
                }

                if (!File.Exists(scriptPath) ||
                    !Path.GetExtension(scriptPath).Equals(".js", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new(
                        $"{prefix}.script_paths",
                        "script_file",
                        localizer.Format("Validation_JavaScriptFile", scriptPath),
                        dictionary.Id));
                }
            }
        }

        return new SettingsValidationResult(errors.ToImmutable());
    }

    private static bool ValidatePathSyntax(
        string path,
        string settingsPath,
        DictionaryId dictionaryId,
        ImmutableArray<SettingsValidationError>.Builder errors,
        IAppLocalizer localizer)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) ||
                path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                throw new ArgumentException("Path is empty or contains invalid characters.");
            }

            _ = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add(new(
                settingsPath,
                "path_syntax",
                localizer.Format("Validation_PathSyntax", path),
                dictionaryId));
            return false;
        }
    }

    private static ImmutableArray<string> NormalizePaths(ImmutableArray<string> paths)
    {
        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static string NormalizeFont(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
