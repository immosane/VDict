using System.Collections.Immutable;

namespace VDict.Core.Models;

public readonly record struct DictionaryId
{
    public DictionaryId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static bool TryParse(string? value, out DictionaryId id)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Contains('\0'))
        {
            id = default;
            return false;
        }

        id = new DictionaryId(value);
        return true;
    }
}

public enum DictionaryInteractionMode
{
    Auto,
    Native,
    Fallback
}

public enum DictionaryDarkModeStrategy
{
    Global,
    Light,
    Dark
}

public enum LocalScriptMode
{
    Standard,
    Legacy,
    Strict
}

public enum CompatibilityDiagnosticsLevel
{
    Off,
    Standard,
    Detailed
}

public sealed record DictionaryAppearance(
    int FontSize,
    double LineHeight,
    string LatinFontFamily,
    string CjkFontFamily,
    bool OverrideFont,
    DictionaryInteractionMode InteractionMode,
    DictionaryDarkModeStrategy DarkModeStrategy)
{
    public bool OverrideTypography { get; init; }

    public static DictionaryAppearance Default { get; } = new(
        18,
        1.65,
        "Segoe UI",
        "Microsoft YaHei",
        false,
        DictionaryInteractionMode.Auto,
        DictionaryDarkModeStrategy.Light);

    public static DictionaryAppearance NewDictionaryDefault { get; } =
        Default with { DarkModeStrategy = DictionaryDarkModeStrategy.Global };
}

public sealed record DictionaryDefinition(
    DictionaryId Id,
    string Name,
    string? DisplayName,
    string MdxPath,
    bool Enabled,
    bool BlockNetwork,
    ImmutableArray<string> MddPaths,
    ImmutableArray<string> CssPaths,
    DictionaryAppearance Appearance)
{
    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    public LocalScriptMode LocalScriptMode { get; init; } = LocalScriptMode.Standard;

    public ImmutableArray<string> ScriptPaths { get; init; } = [];
}

public static class DictionaryScriptDiscovery
{
    public static ImmutableArray<string> GetEffectivePaths(DictionaryDefinition dictionary)
    {
        ImmutableArray<string>.Builder paths = ImmutableArray.CreateBuilder<string>();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in dictionary.ScriptPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            Add(path.Trim());
        }

        string? automatic = FindSameStemScript(dictionary.MdxPath);
        if (automatic is not null)
        {
            Add(automatic);
        }

        return paths.ToImmutable();

        void Add(string path)
        {
            string normalized;
            try
            {
                normalized = Path.GetFullPath(path);
            }
            catch (Exception exception)
                when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                normalized = path;
            }

            if (seen.Add(normalized))
            {
                paths.Add(normalized);
            }
        }
    }

    public static string? FindSameStemScript(string mdxPath)
    {
        if (string.IsNullOrWhiteSpace(mdxPath))
        {
            return null;
        }

        try
        {
            string fullMdxPath = Path.GetFullPath(mdxPath);
            string? directory = Path.GetDirectoryName(fullMdxPath);
            if (directory is null || !Directory.Exists(directory))
            {
                return null;
            }

            string expectedName = Path.GetFileNameWithoutExtension(fullMdxPath) + ".js";
            return Directory.EnumerateFiles(directory, "*.js", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => Path.GetFileName(path).Equals(
                    expectedName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException or
                  NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

public enum ApplicationTheme
{
    System,
    Light,
    Dark
}

public enum ApplicationLanguage
{
    System,
    ChineseSimplified,
    English
}

public enum ExternalLinkPolicy
{
    Block,
    SystemBrowser
}

public sealed record ApplicationAppearance(
    int FontSize,
    double LineHeight,
    double ContentZoom,
    int ContentWidthPercent,
    ApplicationTheme Theme,
    string AppFontFamily,
    string LatinFontFamily,
    string CjkFontFamily,
    double? CandidatePaneWidth)
{
    public static ApplicationAppearance Default { get; } = new(
        18,
        1.65,
        1.0,
        80,
        ApplicationTheme.System,
        "Microsoft YaHei UI",
        "Segoe UI",
        "Microsoft YaHei",
        null);
}

public sealed record ApplicationSettings(
    long Revision,
    ImmutableArray<DictionaryDefinition> Dictionaries,
    ApplicationAppearance Appearance,
    bool ReduceDictionaryBrightnessInDarkMode,
    ExternalLinkPolicy ExternalLinkPolicy,
    bool CompatibilityDiagnosticsEnabled)
{
    public ApplicationLanguage UiLanguage { get; init; } = ApplicationLanguage.System;

    public bool DetailedDiagnosticsEnabled { get; init; }

    public CompatibilityDiagnosticsLevel DiagnosticsLevel =>
        !CompatibilityDiagnosticsEnabled
            ? CompatibilityDiagnosticsLevel.Off
            : DetailedDiagnosticsEnabled
                ? CompatibilityDiagnosticsLevel.Detailed
                : CompatibilityDiagnosticsLevel.Standard;

    public static ApplicationSettings Default { get; } = new(
        0,
        [],
        ApplicationAppearance.Default,
        true,
        ExternalLinkPolicy.Block,
        false);
}
