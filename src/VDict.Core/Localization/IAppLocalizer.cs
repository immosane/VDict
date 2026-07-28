using System.Globalization;

namespace VDict.Core.Localization;

public interface IAppLocalizer
{
    string Get(string resourceId);

    string Format(string resourceId, params object?[] arguments);
}

public sealed class IdentityAppLocalizer : IAppLocalizer
{
    private IdentityAppLocalizer()
    {
    }

    public static IdentityAppLocalizer Instance { get; } = new();

    public string Get(string resourceId) => resourceId;

    public string Format(string resourceId, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, resourceId, arguments);
}
