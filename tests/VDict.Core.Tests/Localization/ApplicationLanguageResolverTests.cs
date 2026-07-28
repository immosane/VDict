using VDict.Core.Localization;
using VDict.Core.Models;

namespace VDict.Core.Tests.Localization;

public sealed class ApplicationLanguageResolverTests
{
    [Theory]
    [InlineData("zh", ApplicationLanguage.ChineseSimplified)]
    [InlineData("zh-CN", ApplicationLanguage.ChineseSimplified)]
    [InlineData("zh-Hans-CN", ApplicationLanguage.ChineseSimplified)]
    [InlineData("zh-Hant-TW", ApplicationLanguage.ChineseSimplified)]
    [InlineData("en-US", ApplicationLanguage.English)]
    [InlineData("en-GB", ApplicationLanguage.English)]
    [InlineData("ja-JP", ApplicationLanguage.English)]
    [InlineData("", ApplicationLanguage.English)]
    [InlineData(null, ApplicationLanguage.English)]
    public void System_UsesWindowsUiLanguageAndFallsBackToEnglish(
        string? systemLanguageTag,
        ApplicationLanguage expected)
    {
        ApplicationLanguage actual = ApplicationLanguageResolver.Resolve(
            ApplicationLanguage.System,
            systemLanguageTag);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(ApplicationLanguage.ChineseSimplified, "en-US")]
    [InlineData(ApplicationLanguage.English, "zh-CN")]
    public void ExplicitChoice_WinsOverWindowsUiLanguage(
        ApplicationLanguage configured,
        string systemLanguageTag)
    {
        ApplicationLanguage actual = ApplicationLanguageResolver.Resolve(
            configured,
            systemLanguageTag);

        Assert.Equal(configured, actual);
    }
}
