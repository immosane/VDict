using System.Collections.Immutable;
using VDict.Core.Models;

namespace VDict.Core.Settings;

public sealed record SettingsMergeResult(
    ApplicationSettings? Settings,
    ImmutableArray<string> Conflicts)
{
    public bool Succeeded => Settings is not null && Conflicts.IsDefaultOrEmpty;
}

public static class SettingsThreeWayMerger
{
    public static SettingsMergeResult Merge(
        ApplicationSettings @base,
        ApplicationSettings current,
        ApplicationSettings draft)
    {
        ImmutableArray<string>.Builder conflicts = ImmutableArray.CreateBuilder<string>();
        ApplicationAppearance appearance = current.Appearance with
        {
            FontSize = MergeValue(
                "Appearance.FontSize",
                @base.Appearance.FontSize,
                current.Appearance.FontSize,
                draft.Appearance.FontSize,
                conflicts),
            LineHeight = MergeValue(
                "Appearance.LineHeight",
                @base.Appearance.LineHeight,
                current.Appearance.LineHeight,
                draft.Appearance.LineHeight,
                conflicts),
            ContentZoom = MergeValue(
                "Appearance.ContentZoom",
                @base.Appearance.ContentZoom,
                current.Appearance.ContentZoom,
                draft.Appearance.ContentZoom,
                conflicts),
            ContentWidthPercent = MergeValue(
                "Appearance.ContentWidthPercent",
                @base.Appearance.ContentWidthPercent,
                current.Appearance.ContentWidthPercent,
                draft.Appearance.ContentWidthPercent,
                conflicts),
            CandidatePaneWidth = MergeValue(
                "Appearance.CandidatePaneWidth",
                @base.Appearance.CandidatePaneWidth,
                current.Appearance.CandidatePaneWidth,
                draft.Appearance.CandidatePaneWidth,
                conflicts),
            Theme = MergeValue(
                "Appearance.Theme",
                @base.Appearance.Theme,
                current.Appearance.Theme,
                draft.Appearance.Theme,
                conflicts),
            AppFontFamily = MergeValue(
                "Appearance.AppFontFamily",
                @base.Appearance.AppFontFamily,
                current.Appearance.AppFontFamily,
                draft.Appearance.AppFontFamily,
                conflicts),
            LatinFontFamily = MergeValue(
                "Appearance.LatinFontFamily",
                @base.Appearance.LatinFontFamily,
                current.Appearance.LatinFontFamily,
                draft.Appearance.LatinFontFamily,
                conflicts),
            CjkFontFamily = MergeValue(
                "Appearance.CjkFontFamily",
                @base.Appearance.CjkFontFamily,
                current.Appearance.CjkFontFamily,
                draft.Appearance.CjkFontFamily,
                conflicts)
        };

        ImmutableArray<DictionaryDefinition> dictionaries = MergeDictionaries(
            @base.Dictionaries,
            current.Dictionaries,
            draft.Dictionaries,
            conflicts);
        ApplicationSettings merged = current with
        {
            UiLanguage = MergeValue(
                nameof(ApplicationSettings.UiLanguage),
                @base.UiLanguage,
                current.UiLanguage,
                draft.UiLanguage,
                conflicts),
            Appearance = appearance,
            Dictionaries = dictionaries,
            ReduceDictionaryBrightnessInDarkMode = MergeValue(
                nameof(ApplicationSettings.ReduceDictionaryBrightnessInDarkMode),
                @base.ReduceDictionaryBrightnessInDarkMode,
                current.ReduceDictionaryBrightnessInDarkMode,
                draft.ReduceDictionaryBrightnessInDarkMode,
                conflicts),
            ExternalLinkPolicy = MergeValue(
                nameof(ApplicationSettings.ExternalLinkPolicy),
                @base.ExternalLinkPolicy,
                current.ExternalLinkPolicy,
                draft.ExternalLinkPolicy,
                conflicts),
            CompatibilityDiagnosticsEnabled = MergeValue(
                nameof(ApplicationSettings.CompatibilityDiagnosticsEnabled),
                @base.CompatibilityDiagnosticsEnabled,
                current.CompatibilityDiagnosticsEnabled,
                draft.CompatibilityDiagnosticsEnabled,
                conflicts),
            DetailedDiagnosticsEnabled = MergeValue(
                nameof(ApplicationSettings.DetailedDiagnosticsEnabled),
                @base.DetailedDiagnosticsEnabled,
                current.DetailedDiagnosticsEnabled,
                draft.DetailedDiagnosticsEnabled,
                conflicts)
        };
        return conflicts.Count == 0
            ? new SettingsMergeResult(merged, [])
            : new SettingsMergeResult(null, conflicts.ToImmutable());
    }

    private static ImmutableArray<DictionaryDefinition> MergeDictionaries(
        ImmutableArray<DictionaryDefinition> @base,
        ImmutableArray<DictionaryDefinition> current,
        ImmutableArray<DictionaryDefinition> draft,
        ImmutableArray<string>.Builder conflicts)
    {
        if (!TryCreateMap(@base, out Dictionary<DictionaryId, DictionaryDefinition>? baseById) ||
            !TryCreateMap(current, out Dictionary<DictionaryId, DictionaryDefinition>? currentById) ||
            !TryCreateMap(draft, out Dictionary<DictionaryId, DictionaryDefinition>? draftById))
        {
            conflicts.Add("Dictionaries.DuplicateId");
            return current;
        }

        ImmutableArray<DictionaryId> baseOrder = GetOrder(@base);
        ImmutableArray<DictionaryId> currentOrder = GetOrder(current);
        ImmutableArray<DictionaryId> draftOrder = GetOrder(draft);
        bool currentMembershipChanged = !SameMembership(baseOrder, currentOrder);
        bool draftMembershipChanged = !SameMembership(baseOrder, draftOrder);
        bool currentReordered = !currentMembershipChanged &&
                                !currentOrder.SequenceEqual(baseOrder);
        bool draftReordered = !draftMembershipChanged &&
                              !draftOrder.SequenceEqual(baseOrder);

        if (currentMembershipChanged && draftReordered ||
            draftMembershipChanged && currentReordered ||
            currentMembershipChanged && draftMembershipChanged &&
            !SameMembership(currentOrder, draftOrder))
        {
            conflicts.Add("Dictionaries.Membership");
            return current;
        }

        ImmutableArray<DictionaryId> mergedOrder;
        if (currentMembershipChanged && !draftMembershipChanged)
        {
            mergedOrder = currentOrder;
        }
        else if (draftMembershipChanged && !currentMembershipChanged)
        {
            mergedOrder = draftOrder;
        }
        else
        {
            mergedOrder = MergeOrder(baseOrder, currentOrder, draftOrder, conflicts);
        }

        Dictionary<DictionaryId, DictionaryDefinition> mergedById = [];
        foreach (DictionaryId id in baseOrder
                     .Concat(currentOrder)
                     .Concat(draftOrder)
                     .Distinct())
        {
            baseById.TryGetValue(id, out DictionaryDefinition? baseDictionary);
            currentById.TryGetValue(id, out DictionaryDefinition? currentDictionary);
            draftById.TryGetValue(id, out DictionaryDefinition? draftDictionary);
            DictionaryDefinition? result = MergeDictionary(
                id,
                baseDictionary,
                currentDictionary,
                draftDictionary,
                conflicts);
            if (result is not null)
            {
                mergedById[id] = result;
            }
        }

        ImmutableArray<DictionaryDefinition>.Builder merged =
            ImmutableArray.CreateBuilder<DictionaryDefinition>(mergedOrder.Length);
        foreach (DictionaryId id in mergedOrder)
        {
            if (mergedById.TryGetValue(id, out DictionaryDefinition? dictionary))
            {
                merged.Add(dictionary);
            }
        }

        return merged.ToImmutable();
    }

    private static DictionaryDefinition? MergeDictionary(
        DictionaryId id,
        DictionaryDefinition? @base,
        DictionaryDefinition? current,
        DictionaryDefinition? draft,
        ImmutableArray<string>.Builder conflicts)
    {
        string prefix = $"Dictionaries[{id.Value}]";
        if (@base is null)
        {
            if (current is null)
            {
                return draft;
            }

            if (draft is null ||
                ApplicationSettingsComparer.DictionaryContentEquals(current, draft))
            {
                return current;
            }

            conflicts.Add(prefix);
            return current;
        }

        if (current is null)
        {
            if (draft is not null &&
                !ApplicationSettingsComparer.DictionaryContentEquals(@base, draft))
            {
                conflicts.Add(prefix + ".DeletedVsModified");
            }

            return null;
        }

        if (draft is null)
        {
            if (!ApplicationSettingsComparer.DictionaryContentEquals(@base, current))
            {
                conflicts.Add(prefix + ".ModifiedVsDeleted");
                return current;
            }

            return null;
        }

        DictionaryAppearance appearance = current.Appearance with
        {
            FontSize = MergeValue(
                prefix + ".Appearance.FontSize",
                @base.Appearance.FontSize,
                current.Appearance.FontSize,
                draft.Appearance.FontSize,
                conflicts),
            LineHeight = MergeValue(
                prefix + ".Appearance.LineHeight",
                @base.Appearance.LineHeight,
                current.Appearance.LineHeight,
                draft.Appearance.LineHeight,
                conflicts),
            LatinFontFamily = MergeValue(
                prefix + ".Appearance.LatinFontFamily",
                @base.Appearance.LatinFontFamily,
                current.Appearance.LatinFontFamily,
                draft.Appearance.LatinFontFamily,
                conflicts),
            CjkFontFamily = MergeValue(
                prefix + ".Appearance.CjkFontFamily",
                @base.Appearance.CjkFontFamily,
                current.Appearance.CjkFontFamily,
                draft.Appearance.CjkFontFamily,
                conflicts),
            OverrideFont = MergeValue(
                prefix + ".Appearance.OverrideFont",
                @base.Appearance.OverrideFont,
                current.Appearance.OverrideFont,
                draft.Appearance.OverrideFont,
                conflicts),
            OverrideTypography = MergeValue(
                prefix + ".Appearance.OverrideTypography",
                @base.Appearance.OverrideTypography,
                current.Appearance.OverrideTypography,
                draft.Appearance.OverrideTypography,
                conflicts),
            InteractionMode = MergeValue(
                prefix + ".Appearance.InteractionMode",
                @base.Appearance.InteractionMode,
                current.Appearance.InteractionMode,
                draft.Appearance.InteractionMode,
                conflicts),
            DarkModeStrategy = MergeValue(
                prefix + ".Appearance.DarkModeStrategy",
                @base.Appearance.DarkModeStrategy,
                current.Appearance.DarkModeStrategy,
                draft.Appearance.DarkModeStrategy,
                conflicts)
        };

        return current with
        {
            Name = MergeValue(prefix + ".Name", @base.Name, current.Name, draft.Name, conflicts),
            DisplayName = MergeValue(
                prefix + ".DisplayName",
                @base.DisplayName,
                current.DisplayName,
                draft.DisplayName,
                conflicts),
            MdxPath = MergeValue(
                prefix + ".MdxPath",
                @base.MdxPath,
                current.MdxPath,
                draft.MdxPath,
                conflicts),
            Enabled = MergeValue(
                prefix + ".Enabled",
                @base.Enabled,
                current.Enabled,
                draft.Enabled,
                conflicts),
            BlockNetwork = MergeValue(
                prefix + ".BlockNetwork",
                @base.BlockNetwork,
                current.BlockNetwork,
                draft.BlockNetwork,
                conflicts),
            MddPaths = MergePaths(
                prefix + ".MddPaths",
                @base.MddPaths,
                current.MddPaths,
                draft.MddPaths,
                conflicts),
            CssPaths = MergePaths(
                prefix + ".CssPaths",
                @base.CssPaths,
                current.CssPaths,
                draft.CssPaths,
                conflicts),
            ScriptPaths = MergePaths(
                prefix + ".ScriptPaths",
                @base.ScriptPaths,
                current.ScriptPaths,
                draft.ScriptPaths,
                conflicts),
            LocalScriptMode = MergeValue(
                prefix + ".LocalScriptMode",
                @base.LocalScriptMode,
                current.LocalScriptMode,
                draft.LocalScriptMode,
                conflicts),
            Appearance = appearance
        };
    }

    private static ImmutableArray<DictionaryId> MergeOrder(
        ImmutableArray<DictionaryId> @base,
        ImmutableArray<DictionaryId> current,
        ImmutableArray<DictionaryId> draft,
        ImmutableArray<string>.Builder conflicts)
    {
        if (draft.SequenceEqual(@base))
        {
            return current;
        }

        if (current.SequenceEqual(@base) || current.SequenceEqual(draft))
        {
            return draft;
        }

        conflicts.Add("Dictionaries.Order");
        return current;
    }

    private static ImmutableArray<string> MergePaths(
        string path,
        ImmutableArray<string> @base,
        ImmutableArray<string> current,
        ImmutableArray<string> draft,
        ImmutableArray<string>.Builder conflicts)
    {
        bool DraftEqualsBase() => draft.SequenceEqual(@base, StringComparer.OrdinalIgnoreCase);
        bool CurrentEqualsBase() => current.SequenceEqual(@base, StringComparer.OrdinalIgnoreCase);
        bool CurrentEqualsDraft() => current.SequenceEqual(draft, StringComparer.OrdinalIgnoreCase);
        if (DraftEqualsBase())
        {
            return current;
        }

        if (CurrentEqualsBase() || CurrentEqualsDraft())
        {
            return draft;
        }

        conflicts.Add(path);
        return current;
    }

    private static bool TryCreateMap(
        ImmutableArray<DictionaryDefinition> dictionaries,
        out Dictionary<DictionaryId, DictionaryDefinition> map)
    {
        map = new Dictionary<DictionaryId, DictionaryDefinition>();
        foreach (DictionaryDefinition dictionary in dictionaries)
        {
            if (!map.TryAdd(dictionary.Id, dictionary))
            {
                return false;
            }
        }

        return true;
    }

    private static ImmutableArray<DictionaryId> GetOrder(
        ImmutableArray<DictionaryDefinition> dictionaries) =>
        dictionaries.Select(dictionary => dictionary.Id).ToImmutableArray();

    private static bool SameMembership(
        ImmutableArray<DictionaryId> left,
        ImmutableArray<DictionaryId> right) =>
        left.Length == right.Length &&
        left.ToHashSet().SetEquals(right);

    private static T MergeValue<T>(
        string path,
        T @base,
        T current,
        T draft,
        ImmutableArray<string>.Builder conflicts)
    {
        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        if (comparer.Equals(draft, @base))
        {
            return current;
        }

        if (comparer.Equals(current, @base) || comparer.Equals(current, draft))
        {
            return draft;
        }

        conflicts.Add(path);
        return current;
    }
}
