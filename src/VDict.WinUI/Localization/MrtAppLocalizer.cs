using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using VDict.Core.Environment;
using VDict.Core.Localization;

namespace VDict.WinUI.Localization;

internal sealed class MrtAppLocalizer : IAppLocalizer
{
    private readonly ResourceLoader _loader;

    public MrtAppLocalizer(IAppEnvironment environment)
    {
        _loader = environment.IsPackaged
            ? new ResourceLoader()
            : new ResourceLoader(
                MrtResourcePath.For(environment),
                "Resources");
    }

    public string Get(string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        string value = _loader.GetString(resourceId.Replace('.', '/'));
        return string.IsNullOrEmpty(value) ? resourceId : value;
    }

    public string Format(string resourceId, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(resourceId), arguments);
}

internal static class MrtResourcePath
{
    public static string For(IAppEnvironment environment) =>
        Path.Combine(environment.ExecutableDirectory, "resources.pri");
}
