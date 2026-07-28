using VDict.Core.Models;

namespace VDict.Core.Settings;

public static class SettingsChangeClassifier
{
    public static SettingsChangeSet Classify(
        ApplicationSettings previous,
        ApplicationSettings current)
    {
        SettingsChangeKind changes = SettingsChangeKind.None;
        ApplicationAppearance oldAppearance = previous.Appearance;
        ApplicationAppearance newAppearance = current.Appearance;

        if (previous.UiLanguage != current.UiLanguage)
        {
            changes |= SettingsChangeKind.UiLanguage;
        }

        if (oldAppearance.ContentZoom != newAppearance.ContentZoom)
        {
            changes |= SettingsChangeKind.Zoom;
        }

        if (oldAppearance.CandidatePaneWidth != newAppearance.CandidatePaneWidth)
        {
            changes |= SettingsChangeKind.MainWindowLayout;
        }

        if (oldAppearance.Theme != newAppearance.Theme ||
            oldAppearance.AppFontFamily != newAppearance.AppFontFamily)
        {
            changes |= SettingsChangeKind.ApplicationTheme;
        }

        if (oldAppearance.FontSize != newAppearance.FontSize ||
            oldAppearance.LineHeight != newAppearance.LineHeight ||
            oldAppearance.ContentWidthPercent != newAppearance.ContentWidthPercent ||
            oldAppearance.LatinFontFamily != newAppearance.LatinFontFamily ||
            oldAppearance.CjkFontFamily != newAppearance.CjkFontFamily ||
            previous.ReduceDictionaryBrightnessInDarkMode !=
            current.ReduceDictionaryBrightnessInDarkMode)
        {
            changes |= SettingsChangeKind.ReaderAppearance;
        }

        if (previous.ExternalLinkPolicy != current.ExternalLinkPolicy)
        {
            changes |= SettingsChangeKind.SecurityPolicy;
        }

        if (previous.CompatibilityDiagnosticsEnabled !=
                current.CompatibilityDiagnosticsEnabled ||
            previous.DetailedDiagnosticsEnabled !=
                current.DetailedDiagnosticsEnabled)
        {
            changes |= SettingsChangeKind.Diagnostics;
        }

        ClassifyDictionaries(previous, current, ref changes);
        return new SettingsChangeSet(changes);
    }

    private static void ClassifyDictionaries(
        ApplicationSettings previous,
        ApplicationSettings current,
        ref SettingsChangeKind changes)
    {
        if (!previous.Dictionaries.Select(item => item.Id)
                .SequenceEqual(current.Dictionaries.Select(item => item.Id)) ||
            previous.Dictionaries.Length != current.Dictionaries.Length)
        {
            changes |= SettingsChangeKind.CatalogStructure;
        }

        Dictionary<DictionaryId, DictionaryDefinition> oldById =
            previous.Dictionaries.ToDictionary(item => item.Id);
        foreach (DictionaryDefinition dictionary in current.Dictionaries)
        {
            if (!oldById.TryGetValue(dictionary.Id, out DictionaryDefinition? old))
            {
                changes |= SettingsChangeKind.CatalogStructure;
                continue;
            }

            if (old.Appearance != dictionary.Appearance ||
                old.DisplayName != dictionary.DisplayName)
            {
                changes |= SettingsChangeKind.DictionaryPresentation;
            }

            if (old.Enabled != dictionary.Enabled ||
                old.Name != dictionary.Name ||
                old.MdxPath != dictionary.MdxPath ||
                old.BlockNetwork != dictionary.BlockNetwork ||
                old.LocalScriptMode != dictionary.LocalScriptMode ||
                !old.MddPaths.SequenceEqual(
                    dictionary.MddPaths,
                    StringComparer.OrdinalIgnoreCase) ||
                !old.CssPaths.SequenceEqual(
                    dictionary.CssPaths,
                    StringComparer.OrdinalIgnoreCase) ||
                !old.ScriptPaths.SequenceEqual(
                    dictionary.ScriptPaths,
                    StringComparer.OrdinalIgnoreCase))
            {
                changes |= SettingsChangeKind.CatalogStructure;
            }

            if (old.BlockNetwork != dictionary.BlockNetwork ||
                old.LocalScriptMode != dictionary.LocalScriptMode)
            {
                changes |= SettingsChangeKind.SecurityPolicy;
            }
        }
    }
}
