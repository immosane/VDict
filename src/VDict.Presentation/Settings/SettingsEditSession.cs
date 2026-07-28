using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using VDict.Core.Catalog;
using VDict.Core.Localization;
using VDict.Core.Models;
using VDict.Core.Settings;

namespace VDict.Presentation.Settings;

[Flags]
public enum SettingsDirtySection
{
    None = 0,
    General = 1 << 0,
    Dictionaries = 1 << 1,
    Appearance = 1 << 2,
    Reader = 1 << 3,
    SecurityAndNetwork = 1 << 4,
    StorageAndCache = 1 << 5
}

public sealed partial class SettingsEditSession : ObservableObject
{
    private readonly IDictionaryCatalog _catalog;
    private readonly IAppLocalizer _localizer;
    private long _draftVersion;

    [ObservableProperty]
    private ApplicationSettings _original;

    [ObservableProperty]
    private ApplicationSettings _draft;

    [ObservableProperty]
    private SettingsDirtySection _dirtySections;

    [ObservableProperty]
    private SettingsValidationResult _validation = SettingsValidationResult.Valid;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string? _saveError;

    public SettingsEditSession(
        ApplicationSettings settings,
        IDictionaryCatalog catalog,
        IAppLocalizer? localizer = null)
    {
        _catalog = catalog;
        _localizer = localizer ?? IdentityAppLocalizer.Instance;
        _original = settings;
        _draft = settings;
    }

    public bool IsDirty => DirtySections != SettingsDirtySection.None;

    public long DraftVersion => Interlocked.Read(ref _draftVersion);

    public void Update(
        Func<ApplicationSettings, ApplicationSettings> update,
        SettingsDirtySection section)
    {
        ArgumentNullException.ThrowIfNull(update);
        ApplicationSettings updated = update(Draft);
        Draft = updated with
        {
            Dictionaries = PreserveKnownDictionarySources(
                Draft.Dictionaries,
                updated.Dictionaries)
        };
        Interlocked.Increment(ref _draftVersion);
        RecalculateDirtySections(section);
        Validate();
    }

    public void AddDictionary(DictionaryDefinition dictionary)
    {
        Draft = Draft with { Dictionaries = Draft.Dictionaries.Add(dictionary) };
        Interlocked.Increment(ref _draftVersion);
        RecalculateDirtySections(SettingsDirtySection.Dictionaries);
        Validate();
    }

    public bool RemoveDictionary(DictionaryId dictionaryId)
    {
        int index = Draft.Dictionaries.FindIndex(item => item.Id == dictionaryId);
        if (index < 0)
        {
            return false;
        }

        Draft = Draft with { Dictionaries = Draft.Dictionaries.RemoveAt(index) };
        Interlocked.Increment(ref _draftVersion);
        RecalculateDirtySections(SettingsDirtySection.Dictionaries);
        Validate();
        return true;
    }

    public bool ReplaceDictionary(DictionaryDefinition dictionary)
    {
        int index = Draft.Dictionaries.FindIndex(item => item.Id == dictionary.Id);
        if (index < 0)
        {
            return false;
        }

        DictionaryDefinition existing = Draft.Dictionaries[index];
        DictionaryDefinition updated = PreserveDictionarySource(existing, dictionary);
        Draft = Draft with { Dictionaries = Draft.Dictionaries.SetItem(index, updated) };
        Interlocked.Increment(ref _draftVersion);
        RecalculateDirtySections(SettingsDirtySection.Dictionaries);
        Validate();
        return true;
    }

    public bool MoveDictionary(DictionaryId dictionaryId, int destinationIndex)
    {
        int sourceIndex = Draft.Dictionaries.FindIndex(item => item.Id == dictionaryId);
        if (sourceIndex < 0 || destinationIndex < 0 || destinationIndex >= Draft.Dictionaries.Length)
        {
            return false;
        }

        DictionaryDefinition item = Draft.Dictionaries[sourceIndex];
        ImmutableArray<DictionaryDefinition> without = Draft.Dictionaries.RemoveAt(sourceIndex);
        Draft = Draft with
        {
            Dictionaries = without.Insert(Math.Min(destinationIndex, without.Length), item)
        };
        Interlocked.Increment(ref _draftVersion);
        RecalculateDirtySections(SettingsDirtySection.Dictionaries);
        Validate();
        return true;
    }

    public void RestoreReaderDefaults()
    {
        Draft = Draft with
        {
            Appearance = Draft.Appearance with
            {
                FontSize = ApplicationAppearance.Default.FontSize,
                LineHeight = ApplicationAppearance.Default.LineHeight,
                ContentZoom = ApplicationAppearance.Default.ContentZoom,
                ContentWidthPercent = ApplicationAppearance.Default.ContentWidthPercent,
                LatinFontFamily = ApplicationAppearance.Default.LatinFontFamily,
                CjkFontFamily = ApplicationAppearance.Default.CjkFontFamily
            },
            ReduceDictionaryBrightnessInDarkMode =
                ApplicationSettings.Default.ReduceDictionaryBrightnessInDarkMode
        };
        Interlocked.Increment(ref _draftVersion);
        RecalculateDirtySections(SettingsDirtySection.Reader);
        Validate();
    }

    public bool RestoreDictionaryAppearance(DictionaryId dictionaryId)
    {
        int index = Draft.Dictionaries.FindIndex(item => item.Id == dictionaryId);
        if (index < 0)
        {
            return false;
        }

        DictionaryDefinition dictionary = Draft.Dictionaries[index] with
        {
            Appearance = DictionaryAppearance.Default
        };
        Draft = Draft with
        {
            Dictionaries = Draft.Dictionaries.SetItem(index, dictionary)
        };
        Interlocked.Increment(ref _draftVersion);
        RecalculateDirtySections(SettingsDirtySection.Dictionaries);
        Validate();
        return true;
    }

    public void RestoreAllDefaults()
    {
        Draft = Draft with
        {
            Appearance = ApplicationAppearance.Default,
            UiLanguage = ApplicationSettings.Default.UiLanguage,
            ReduceDictionaryBrightnessInDarkMode =
                ApplicationSettings.Default.ReduceDictionaryBrightnessInDarkMode,
            ExternalLinkPolicy = ApplicationSettings.Default.ExternalLinkPolicy,
            CompatibilityDiagnosticsEnabled =
                ApplicationSettings.Default.CompatibilityDiagnosticsEnabled,
            DetailedDiagnosticsEnabled =
                ApplicationSettings.Default.DetailedDiagnosticsEnabled,
            Dictionaries = Draft.Dictionaries
                .Select(dictionary => dictionary with { Appearance = DictionaryAppearance.Default })
                .ToImmutableArray()
        };
        Interlocked.Increment(ref _draftVersion);
        RecalculateDirtySections(SettingsDirtySection.None);
        Validate();
    }

    public void RestoreDefaults() => RestoreAllDefaults();

    public void Cancel()
    {
        Draft = Original;
        Interlocked.Increment(ref _draftVersion);
        DirtySections = SettingsDirtySection.None;
        Validation = SettingsValidationResult.Valid;
        SaveError = null;
        OnPropertyChanged(nameof(IsDirty));
    }

    public async Task<SettingsCommitResult> SaveAsync(
        CancellationToken cancellationToken = default,
        IProgress<SettingsOperationProgress>? progress = null)
    {
        if (IsSaving)
        {
            return new SettingsCommitResult(
                false,
                Original.Revision,
                0,
                _localizer.Get("Settings_Save_AlreadySaving"));
        }

        if (!IsDirty)
        {
            DictionaryCatalogSnapshot snapshot = _catalog.GetSnapshot();
            return new SettingsCommitResult(
                true,
                snapshot.SettingsRevision,
                snapshot.Generation,
                null);
        }

        Validate();
        if (!Validation.IsValid)
        {
            SaveError = string.Join(
                System.Environment.NewLine,
                Validation.Errors.Select(error =>
                {
                    DictionaryDefinition? dictionary = error.DictionaryId is { } id
                        ? Draft.Dictionaries.FirstOrDefault(item => item.Id == id)
                        : null;
                    return dictionary is null
                        ? error.Message
                        : $"{dictionary.EffectiveDisplayName}：{error.Message}";
                }));
            return new SettingsCommitResult(
                false,
                Original.Revision,
                0,
                SaveError);
        }

        IsSaving = true;
        SaveError = null;
        ApplicationSettings savingDraft = Draft;
        long savingVersion = DraftVersion;
        try
        {
            SettingsPrepareResult preparation = await _catalog.PrepareUpdateAsync(
                    savingDraft,
                    cancellationToken,
                    progress);
            if (!preparation.Succeeded || preparation.Plan is null)
            {
                SaveError = string.Join(
                    System.Environment.NewLine,
                    preparation.Errors.Select(error =>
                    {
                        DictionaryDefinition? dictionary = error.DictionaryId is { } id
                            ? Draft.Dictionaries.FirstOrDefault(item => item.Id == id)
                            : null;
                        return dictionary is null
                            ? error.Message
                            : $"{dictionary.EffectiveDisplayName}：{error.Message}";
                    }));
                return new SettingsCommitResult(false, Original.Revision, 0, SaveError);
            }

            progress?.Report(new SettingsOperationProgress(
                "commit",
                _localizer.Get("Settings_Progress_Committing"),
                0,
                0));
            SettingsCommitResult commit = await _catalog.CommitUpdateAsync(
                preparation.Plan,
                cancellationToken);
            if (!commit.Succeeded)
            {
                ApplicationSettings current = _catalog.GetSettingsSnapshot();
                if (current.Revision != preparation.Plan.BaseRevision)
                {
                    SettingsMergeResult merge = SettingsThreeWayMerger.Merge(
                        Original,
                        current,
                        savingDraft);
                    if (!merge.Succeeded || merge.Settings is null)
                    {
                        SaveError = _localizer.Format(
                            "Settings_Save_Conflict",
                            string.Join(", ", merge.Conflicts));
                        return new SettingsCommitResult(
                            false,
                            current.Revision,
                            _catalog.GetSnapshot().Generation,
                            SaveError);
                    }

                    preparation = await _catalog.PrepareUpdateAsync(
                        merge.Settings,
                        cancellationToken,
                        progress);
                    if (!preparation.Succeeded || preparation.Plan is null)
                    {
                        SaveError = string.Join(
                            System.Environment.NewLine,
                            preparation.Errors.Select(error => error.Message));
                        return new SettingsCommitResult(
                            false,
                            current.Revision,
                            _catalog.GetSnapshot().Generation,
                            SaveError);
                    }

                    commit = await _catalog.CommitUpdateAsync(
                        preparation.Plan,
                        cancellationToken);
                }
            }

            if (!commit.Succeeded)
            {
                SaveError = commit.Error;
                return commit;
            }

            Original = preparation.Plan.Settings;
            if (DraftVersion == savingVersion)
            {
                Draft = Original;
                DirtySections = SettingsDirtySection.None;
                Validation = SettingsValidationResult.Valid;
            }
            else
            {
                Draft = Draft with { Revision = Original.Revision };
                RecalculateDirtySections(SettingsDirtySection.None);
                Validate();
            }
            OnPropertyChanged(nameof(IsDirty));
            progress?.Report(new SettingsOperationProgress(
                "complete",
                _localizer.Get("Settings_Progress_Complete"),
                1,
                1));
            return commit;
        }
        catch (OperationCanceledException)
        {
            SaveError = _localizer.Get("Settings_Save_CanceledDraftKept");
            throw;
        }
        catch (Exception)
        {
            SaveError = _localizer.Get("Settings_Save_Failed");
            return new SettingsCommitResult(false, Original.Revision, 0, SaveError);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void Validate() =>
        Validation = ApplicationSettingsNormalizer.Validate(Draft, _localizer);

    private void RecalculateDirtySections(SettingsDirtySection changedSection)
    {
        _ = changedSection;
        SettingsDirtySection sections = SettingsDirtySection.None;
        if (Original.CompatibilityDiagnosticsEnabled != Draft.CompatibilityDiagnosticsEnabled ||
            Original.DetailedDiagnosticsEnabled != Draft.DetailedDiagnosticsEnabled ||
            Original.UiLanguage != Draft.UiLanguage)
        {
            sections |= SettingsDirtySection.General;
        }

        if (Original.Dictionaries.Length != Draft.Dictionaries.Length ||
            !Original.Dictionaries.Zip(Draft.Dictionaries)
                .All(pair => ApplicationSettingsComparer.DictionaryContentEquals(
                    pair.First,
                    pair.Second)))
        {
            sections |= SettingsDirtySection.Dictionaries;
        }

        ApplicationAppearance originalAppearance = Original.Appearance;
        ApplicationAppearance draftAppearance = Draft.Appearance;
        if (originalAppearance.Theme != draftAppearance.Theme ||
            originalAppearance.AppFontFamily != draftAppearance.AppFontFamily ||
            originalAppearance.CandidatePaneWidth != draftAppearance.CandidatePaneWidth)
        {
            sections |= SettingsDirtySection.Appearance;
        }

        if (originalAppearance.FontSize != draftAppearance.FontSize ||
            originalAppearance.LineHeight != draftAppearance.LineHeight ||
            originalAppearance.ContentZoom != draftAppearance.ContentZoom ||
            originalAppearance.ContentWidthPercent != draftAppearance.ContentWidthPercent ||
            originalAppearance.LatinFontFamily != draftAppearance.LatinFontFamily ||
            originalAppearance.CjkFontFamily != draftAppearance.CjkFontFamily ||
            Original.ReduceDictionaryBrightnessInDarkMode !=
            Draft.ReduceDictionaryBrightnessInDarkMode)
        {
            sections |= SettingsDirtySection.Reader;
        }

        if (Original.ExternalLinkPolicy != Draft.ExternalLinkPolicy)
        {
            sections |= SettingsDirtySection.SecurityAndNetwork;
        }

        DirtySections = sections;
        OnPropertyChanged(nameof(IsDirty));
    }

    private static ImmutableArray<DictionaryDefinition> PreserveKnownDictionarySources(
        ImmutableArray<DictionaryDefinition> current,
        ImmutableArray<DictionaryDefinition> updated)
    {
        Dictionary<DictionaryId, DictionaryDefinition> known = [];
        foreach (DictionaryDefinition dictionary in current)
        {
            known.TryAdd(dictionary.Id, dictionary);
        }

        return updated
            .Select(dictionary => known.TryGetValue(dictionary.Id, out DictionaryDefinition? existing)
                ? PreserveDictionarySource(existing, dictionary)
                : dictionary)
            .ToImmutableArray();
    }

    private static DictionaryDefinition PreserveDictionarySource(
        DictionaryDefinition existing,
        DictionaryDefinition updated) =>
        updated with
        {
            MdxPath = existing.MdxPath,
            MddPaths = existing.MddPaths,
            CssPaths = existing.CssPaths,
            ScriptPaths = existing.ScriptPaths
        };
}

internal static class ImmutableArrayExtensions
{
    public static int FindIndex<T>(this ImmutableArray<T> values, Func<T, bool> predicate)
    {
        for (int index = 0; index < values.Length; index++)
        {
            if (predicate(values[index]))
            {
                return index;
            }
        }

        return -1;
    }
}
