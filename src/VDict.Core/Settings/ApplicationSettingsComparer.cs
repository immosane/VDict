using VDict.Core.Models;

namespace VDict.Core.Settings;

public static class ApplicationSettingsComparer
{
    public static bool ContentEquals(ApplicationSettings? left, ApplicationSettings? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null ||
            right is null ||
            left.Revision != right.Revision ||
            left.UiLanguage != right.UiLanguage ||
            left.Appearance != right.Appearance ||
            left.ReduceDictionaryBrightnessInDarkMode != right.ReduceDictionaryBrightnessInDarkMode ||
            left.ExternalLinkPolicy != right.ExternalLinkPolicy ||
            left.CompatibilityDiagnosticsEnabled != right.CompatibilityDiagnosticsEnabled ||
            left.DetailedDiagnosticsEnabled != right.DetailedDiagnosticsEnabled ||
            left.Dictionaries.Length != right.Dictionaries.Length)
        {
            return false;
        }

        for (int index = 0; index < left.Dictionaries.Length; index++)
        {
            if (!DictionaryContentEquals(left.Dictionaries[index], right.Dictionaries[index]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool DictionaryContentEquals(
        DictionaryDefinition left,
        DictionaryDefinition right)
    {
        return left.Id == right.Id &&
               left.Name == right.Name &&
               left.DisplayName == right.DisplayName &&
               left.MdxPath == right.MdxPath &&
               left.Enabled == right.Enabled &&
               left.BlockNetwork == right.BlockNetwork &&
               left.LocalScriptMode == right.LocalScriptMode &&
               left.Appearance == right.Appearance &&
               left.MddPaths.SequenceEqual(right.MddPaths, StringComparer.OrdinalIgnoreCase) &&
               left.CssPaths.SequenceEqual(right.CssPaths, StringComparer.OrdinalIgnoreCase) &&
               left.ScriptPaths.SequenceEqual(right.ScriptPaths, StringComparer.OrdinalIgnoreCase);
    }
}
