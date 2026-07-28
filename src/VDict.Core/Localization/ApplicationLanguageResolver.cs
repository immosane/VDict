using VDict.Core.Models;

namespace VDict.Core.Localization;

public static class ApplicationLanguageResolver
{
    public static ApplicationLanguage Resolve(
        ApplicationLanguage configuredLanguage,
        string? systemLanguageTag)
    {
        if (configuredLanguage == ApplicationLanguage.ChineseSimplified)
        {
            return ApplicationLanguage.ChineseSimplified;
        }

        if (configuredLanguage == ApplicationLanguage.English)
        {
            return ApplicationLanguage.English;
        }

        string normalized = systemLanguageTag?.Trim() ?? string.Empty;
        return normalized.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)
            ? ApplicationLanguage.ChineseSimplified
            : ApplicationLanguage.English;
    }
}
