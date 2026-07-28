using System.Collections.Immutable;
using Microsoft.Win32;
using VDict.Core.Environment;

namespace VDict.Infrastructure.Environment;

public sealed class WindowsFontCatalogService : IFontCatalogService
{
    private readonly Lazy<ImmutableArray<string>> _fontFamilies = new(Load);

    public IReadOnlyList<string> GetInstalledFontFamilies() => _fontFamilies.Value;

    private static ImmutableArray<string> Load()
    {
        HashSet<string> names = new(StringComparer.CurrentCultureIgnoreCase)
        {
            "Microsoft YaHei UI",
            "Microsoft YaHei",
            "Segoe UI"
        };
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? fonts = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts",
                    writable: false);
                if (fonts is null)
                {
                    continue;
                }

                foreach (string valueName in fonts.GetValueNames())
                {
                    string normalized = Normalize(valueName);
                    if (normalized.Length > 0)
                    {
                        names.Add(normalized);
                    }
                }
            }
            catch (Exception exception)
                when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
            }
        }

        return names.Order(StringComparer.CurrentCultureIgnoreCase).ToImmutableArray();
    }

    private static string Normalize(string value)
    {
        string name = value.Trim();
        int suffix = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (suffix > 0 && name.EndsWith(')'))
        {
            name = name[..suffix].Trim();
        }

        int ampersand = name.IndexOf(" & ", StringComparison.Ordinal);
        return ampersand > 0 ? name[..ampersand].Trim() : name;
    }
}
