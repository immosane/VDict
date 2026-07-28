using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using VDict.Core.Catalog;
using VDict.Core.Environment;
using VDict.Core.Localization;
using VDict.Core.Models;
using VDict.Core.Policies;
using VDict.Core.Settings;
using VDict.Infrastructure.Indexing;
using VDict.Infrastructure.Settings;

namespace VDict.Infrastructure.Catalog;

public sealed class DictionaryCatalog : IDictionaryCatalog, IAsyncDisposable
{
    private static readonly ConcurrentDictionary<FileHashStamp, string> FileHashCache = new();
    private readonly object _stateSync = new();
    private readonly SemaphoreSlim _settingsCommitGate = new(1, 1);
    private readonly DictionaryIndexBuilder _indexBuilder;
    private readonly IAppLocalizer _localizer;
    private readonly AtomicSettingsStore _settingsStore;
    private readonly IAppDataPaths _paths;
    private readonly DictionaryMemoryCache _memoryCache = new();
    private readonly object _planOwner = new();
    private CatalogState _state = CatalogState.Empty;
    private bool _disposed;

    public DictionaryCatalog(
        IAppDataPaths paths,
        AtomicSettingsStore settingsStore,
        DictionaryIndexBuilder indexBuilder,
        IAppLocalizer? localizer = null)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _indexBuilder = indexBuilder;
        _localizer = localizer ?? IdentityAppLocalizer.Instance;
    }

    public event EventHandler<SettingsCommittedEvent>? SettingsCommitted;

    public async Task<SettingsLoadResult> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        SettingsLoadResult load = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        ApplicationSettings settings = load.Settings;
        lock (_stateSync)
        {
            _state = new CatalogState(
                settings,
                0,
                ImmutableDictionary<DictionaryId, DictionaryStore>.Empty);
        }

        if (settings.Dictionaries.Any(dictionary => dictionary.Enabled))
        {
            SettingsPrepareResult preparation = await PrepareUpdateCoreAsync(
                    settings,
                    settings.Revision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (preparation.Succeeded && preparation.Plan is not null)
            {
                SettingsCommitResult commit = await CommitPreparedAsync(
                    preparation.Plan,
                    persistSettings: load.Migration.Steps.Length > 0,
                    cancellationToken).ConfigureAwait(false);
                if (!commit.Succeeded)
                {
                    throw new InvalidOperationException(commit.Error);
                }
            }
            else
            {
                throw new InvalidOperationException(
                    string.Join(
                        System.Environment.NewLine,
                        preparation.Errors.Select(error => error.Message)));
            }
        }
        else
        {
            PublishSettingsCommitted(
                GetSnapshot(),
                new SettingsChangeSet(SettingsChangeKind.CatalogStructure));
        }

        return load;
    }

    public DictionaryCatalogSnapshot GetSnapshot()
    {
        lock (_stateSync)
        {
            return _state.ToPublicSnapshot();
        }
    }

    public ApplicationSettings GetSettingsSnapshot()
    {
        lock (_stateSync)
        {
            return _state.Settings;
        }
    }

    internal DictionaryMemoryCacheStatistics GetMemoryCacheStatistics() =>
        _memoryCache.GetStatistics();

    internal void TrimMemoryCachesForPressure() =>
        _memoryCache.TrimForMemoryPressure();

    public async Task<SuggestionResult> SuggestAsync(
        LookupQuery query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        string text = query.Text.Trim();
        if (text.Length == 0 || limit <= 0)
        {
            DictionaryCatalogSnapshot emptySnapshot = GetSnapshot();
            return new SuggestionResult(text, query.SelectedDictionaryId, emptySnapshot.Generation, []);
        }

        (long Generation, List<DictionaryStore.StoreLease> Leases) captured =
            CaptureLeases(query.SelectedDictionaryId);
        using LeaseCollection leases = new(captured.Leases);
        int perStoreLimit = Math.Max(limit, Math.Min(limit * 2, 500));
        ImmutableArray<string>[] prefixResults = await Task.WhenAll(
            captured.Leases.Select(lease =>
                lease.Store.SuggestPrefixAsync(text, perStoreLimit, cancellationToken))).ConfigureAwait(false);
        ImmutableArray<string> output = MergeSuggestions(prefixResults, limit, []);

        if (output.Length < limit && text.EnumerateRunes().Count() >= 2)
        {
            ImmutableArray<string>[] containsResults = await Task.WhenAll(
                captured.Leases.Select(lease =>
                    lease.Store.SuggestContainsAsync(text, perStoreLimit, cancellationToken))).ConfigureAwait(false);
            output = MergeSuggestions(containsResults, limit, output);
        }

        return new SuggestionResult(
            text,
            query.SelectedDictionaryId,
            captured.Generation,
            output);
    }

    public async Task<ImmutableArray<LookupResult>> LookupAsync(
        LookupQuery query,
        CancellationToken cancellationToken = default) =>
        (await LookupBatchAsync(query, cancellationToken).ConfigureAwait(false)).Results;

    public async Task<LookupBatchResult> LookupBatchAsync(
        LookupQuery query,
        CancellationToken cancellationToken = default)
    {
        string word = query.Text.Trim();
        if (word.Length == 0)
        {
            return new([], []);
        }

        (_, List<DictionaryStore.StoreLease> captured) = CaptureLeases(query.SelectedDictionaryId);
        using LeaseCollection leases = new(captured);
        (LookupResult? Result, string? Notice)[] outcomes = await Task.WhenAll(
            captured.Select(lease => LookupOneAsync(lease, word, cancellationToken)))
            .ConfigureAwait(false);
        return new LookupBatchResult(
            outcomes
                .Where(outcome => outcome.Result is not null)
                .Select(outcome => outcome.Result!)
                .ToImmutableArray(),
            outcomes
                .Where(outcome => !string.IsNullOrWhiteSpace(outcome.Notice))
                .Select(outcome => outcome.Notice!)
                .ToImmutableArray());
    }

    private async Task<(LookupResult? Result, string? Notice)> LookupOneAsync(
        DictionaryStore.StoreLease lease,
        string word,
        CancellationToken cancellationToken)
    {
        try
        {
            LookupResult? result =
                await lease.Store.LookupAsync(word, cancellationToken).ConfigureAwait(false);
            string? notice = result is null &&
                             !string.IsNullOrWhiteSpace(lease.Store.LoadError)
                ? _localizer.Format(
                    "Catalog_ContentUnavailable",
                    lease.Store.Definition.EffectiveDisplayName)
                : null;
            return (result, notice);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (
                null,
                _localizer.Format(
                    "Catalog_ContentUnavailable",
                    lease.Store.Definition.EffectiveDisplayName));
        }
    }

    public async Task<ResourceResolution> ResolveResourceAsync(
        DictionaryId dictionaryId,
        string path,
        CancellationToken cancellationToken = default)
    {
        DictionaryStore.StoreLease? lease;
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lease = _state.Stores.TryGetValue(dictionaryId, out DictionaryStore? store)
                ? store.AcquireLease()
                : null;
        }

        if (lease is null)
        {
            return new ResourceResolution(
                null,
                string.Empty,
                "none",
                _localizer.Get("Catalog_DictionaryNotLoaded"));
        }

        using (lease)
        {
            return await lease.Store.ResolveResourceAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<SettingsPrepareResult> PrepareUpdateAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken = default,
        IProgress<SettingsOperationProgress>? progress = null) =>
        await PrepareUpdateCoreAsync(settings, null, cancellationToken, progress).ConfigureAwait(false);

    private async Task<SettingsPrepareResult> PrepareUpdateCoreAsync(
        ApplicationSettings settings,
        long? targetRevision,
        CancellationToken cancellationToken,
        IProgress<SettingsOperationProgress>? progress = null,
        long? diskBaseRevision = null,
        bool forceIndexRebuild = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        progress?.Report(new SettingsOperationProgress(
            "validation",
            _localizer.Get("Settings_Progress_Validating"),
            0,
            settings.Dictionaries.Length));
        ApplicationSettings normalized = ApplicationSettingsNormalizer.Normalize(settings);
        SettingsValidationResult validation = ApplicationSettingsNormalizer.Validate(normalized);
        ImmutableArray<DictionaryPreparationError>.Builder errors =
            ImmutableArray.CreateBuilder<DictionaryPreparationError>();
        errors.AddRange(validation.Errors.Select(error =>
            new DictionaryPreparationError(error.DictionaryId, error.Message)));
        ApplicationSettings currentSettings;
        long baseRevision;
        lock (_stateSync)
        {
            currentSettings = _state.Settings;
            baseRevision = currentSettings.Revision;
        }

        SettingsChangeSet changes = SettingsChangeClassifier.Classify(
            currentSettings,
            normalized);
        bool requiresCatalogReload =
            changes.RequiresCatalogReload ||
            targetRevision.HasValue &&
            normalized.Dictionaries.Any(dictionary => dictionary.Enabled);
        if (requiresCatalogReload)
        {
            await ValidateFilesAndDuplicatesAsync(
                normalized,
                errors,
                cancellationToken,
                progress).ConfigureAwait(false);
        }

        if (errors.Count > 0)
        {
            return new SettingsPrepareResult(null, errors.ToImmutable());
        }

        ApplicationSettings preparedSettings = normalized with
        {
            Revision = targetRevision ?? (diskBaseRevision ?? baseRevision) + 1
        };
        if (!requiresCatalogReload)
        {
            SettingsCommitPlan lightweightPlan = new(
                preparedSettings,
                baseRevision,
                [],
                new PreparedCatalogUpdate(
                    _planOwner,
                    currentSettings,
                    preparedSettings,
                    [],
                    string.Empty,
                    false))
            {
                DiskBaseRevision = diskBaseRevision
            };
            return new SettingsPrepareResult(lightweightPlan, []);
        }

        string stagingDirectory = Path.Combine(_paths.TempDirectory, $"settings-{Guid.NewGuid():N}");
        List<StagedDictionaryIndex> staged = [];
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            DictionaryDefinition[] enabled =
                preparedSettings.Dictionaries.Where(item => item.Enabled).ToArray();
            for (int dictionaryIndex = 0; dictionaryIndex < enabled.Length; dictionaryIndex++)
            {
                DictionaryDefinition dictionary = enabled[dictionaryIndex];
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new SettingsOperationProgress(
                    "index",
                    _localizer.Format(
                        "Settings_Progress_CheckingIndex",
                        dictionary.EffectiveDisplayName),
                    dictionaryIndex,
                    enabled.Length,
                    dictionary.Id,
                    dictionary.EffectiveDisplayName));
                if (!forceIndexRebuild &&
                    _indexBuilder.GetState(dictionary, _paths.IndexesDirectory) ==
                    DictionaryIndexState.Available)
                {
                    continue;
                }

                Progress<DictionaryIndexProgress>? indexProgress = progress is null
                    ? null
                    : new Progress<DictionaryIndexProgress>(value =>
                        progress.Report(new SettingsOperationProgress(
                            "index",
                            $"{dictionary.EffectiveDisplayName}：{value.Message}",
                            value.Completed,
                            value.Total,
                            dictionary.Id,
                            dictionary.EffectiveDisplayName)));
                staged.Add(await _indexBuilder.PrepareAsync(
                    dictionary,
                    stagingDirectory,
                    indexProgress,
                    cancellationToken: cancellationToken).ConfigureAwait(false));
            }

            for (int dictionaryIndex = 0; dictionaryIndex < enabled.Length; dictionaryIndex++)
            {
                DictionaryDefinition dictionary = enabled[dictionaryIndex];
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new SettingsOperationProgress(
                    "open",
                    _localizer.Format(
                        "Settings_Progress_CheckingDictionary",
                        dictionary.EffectiveDisplayName),
                    dictionaryIndex,
                    enabled.Length,
                    dictionary.Id,
                    dictionary.EffectiveDisplayName));
                string? candidateIndex = staged
                    .FirstOrDefault(item => item.DictionaryId == dictionary.Id)
                    ?.StagedPath;
                if (candidateIndex is null &&
                    !DictionaryIndexPath.TryGet(
                        _paths.IndexesDirectory,
                        dictionary.MdxPath,
                        out candidateIndex))
                {
                    throw new InvalidDataException(
                        _localizer.Format(
                            "Catalog_IndexNotFound",
                            dictionary.EffectiveDisplayName));
                }

                DictionaryStore? reusable;
                lock (_stateSync)
                {
                    reusable = _state.Stores.TryGetValue(
                        dictionary.Id,
                        out DictionaryStore? current) &&
                        current.CanReuseFor(dictionary, candidateIndex)
                        ? current
                        : null;
                }

                if (reusable is not null)
                {
                    continue;
                }

                await using DictionaryStore candidate = await DictionaryStore.OpenFromIndexAsync(
                    dictionary,
                    candidateIndex,
                    _paths.ResourcesDirectory,
                    _memoryCache,
                    cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new SettingsOperationProgress(
                "ready",
                _localizer.Get("Settings_Progress_ReadyToCommit"),
                enabled.Length,
                enabled.Length));

            PreparedCatalogUpdate update = new(
                _planOwner,
                currentSettings,
                preparedSettings,
                staged.ToImmutableArray(),
                stagingDirectory,
                requiresCatalogReload);
            SettingsCommitPlan plan = new(
                preparedSettings,
                baseRevision,
                staged.Select(item => new IndexBuildResult(
                    item.DictionaryId,
                    true,
                    item.StagedPath,
                    null)).ToImmutableArray(),
                update)
            {
                DiskBaseRevision = diskBaseRevision
            };
            return new SettingsPrepareResult(plan, []);
        }
        catch (OperationCanceledException)
        {
            Cleanup(staged, stagingDirectory);
            throw;
        }
        catch (Exception exception)
        {
            Cleanup(staged, stagingDirectory);
            return new SettingsPrepareResult(
                null,
                [new DictionaryPreparationError(null, exception.Message)]);
        }
    }

    public async Task<SettingsCommitResult> CommitUpdateAsync(
        SettingsCommitPlan plan,
        CancellationToken cancellationToken = default)
    {
        await _settingsCommitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SettingsCommitResult commit = await CommitPreparedAsync(
                plan,
                persistSettings: true,
                cancellationToken).ConfigureAwait(false);
            if (commit.Succeeded ||
                commit.CurrentSettings is not { } diskCurrent ||
                plan.ImplementationToken is not PreparedCatalogUpdate update)
            {
                return commit;
            }

            SettingsMergeResult merge = SettingsThreeWayMerger.Merge(
                update.BaseSettings,
                diskCurrent,
                plan.Settings);
            if (!merge.Succeeded || merge.Settings is null)
            {
                return commit with
                {
                    Error = _localizer.Format(
                        "Settings_Save_Conflict",
                        string.Join(", ", merge.Conflicts))
                };
            }

            SettingsPrepareResult retry = await PrepareUpdateCoreAsync(
                merge.Settings,
                targetRevision: null,
                cancellationToken: cancellationToken,
                diskBaseRevision: diskCurrent.Revision).ConfigureAwait(false);
            if (!retry.Succeeded || retry.Plan is null)
            {
                return new SettingsCommitResult(
                    false,
                    diskCurrent.Revision,
                    GetSnapshot().Generation,
                    string.Join(
                        System.Environment.NewLine,
                        retry.Errors.Select(error => error.Message)));
            }

            return await CommitPreparedAsync(
                retry.Plan,
                persistSettings: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _settingsCommitGate.Release();
        }
    }

    public Task<SettingsCommitResult> UpdateZoomAsync(
        double zoom,
        long baseRevision,
        double baseZoom,
        CancellationToken cancellationToken = default) =>
        UpdateAppearanceFieldAsync(
            Math.Round(Math.Clamp(zoom, 0.5, 2.5), 2),
            baseRevision,
            baseZoom,
            appearance => appearance.ContentZoom,
            (appearance, value) => appearance with { ContentZoom = value },
            (left, right) => Math.Abs(left - right) < 0.0001,
            _localizer.Get("Settings_Field_Zoom"),
            cancellationToken);

    public Task<SettingsCommitResult> UpdateCandidatePaneWidthAsync(
        double width,
        long baseRevision,
        double? baseWidth,
        CancellationToken cancellationToken = default) =>
        UpdateAppearanceFieldAsync(
            (double?)Math.Round(Math.Clamp(width, 180, 380), 0),
            baseRevision,
            baseWidth,
            appearance => appearance.CandidatePaneWidth,
            (appearance, value) => appearance with { CandidatePaneWidth = value },
            (left, right) => left == right,
            _localizer.Get("Settings_Field_CandidatePaneWidth"),
            cancellationToken);

    private async Task<SettingsCommitResult> UpdateAppearanceFieldAsync<T>(
        T normalizedValue,
        long baseRevision,
        T baseValue,
        Func<ApplicationAppearance, T> readValue,
        Func<ApplicationAppearance, T, ApplicationAppearance> writeValue,
        Func<T, T, bool> valuesEqual,
        string fieldDisplayName,
        CancellationToken cancellationToken)
    {
        await _settingsCommitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ApplicationSettings previous;
            CatalogState currentState;
            lock (_stateSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                currentState = _state;
                previous = currentState.Settings;
            }

            if (valuesEqual(readValue(previous.Appearance), normalizedValue))
            {
                DictionaryCatalogSnapshot unchanged = currentState.ToPublicSnapshot();
                return new SettingsCommitResult(
                    true,
                    unchanged.SettingsRevision,
                    unchanged.Generation,
                    null);
            }

            if (previous.Revision != baseRevision &&
                !valuesEqual(readValue(previous.Appearance), baseValue))
            {
                DictionaryCatalogSnapshot superseded = currentState.ToPublicSnapshot();
                return new SettingsCommitResult(
                    true,
                    superseded.SettingsRevision,
                    superseded.Generation,
                    null);
            }

            ApplicationSettings updated = previous with
            {
                Revision = previous.Revision + 1,
                Appearance = writeValue(previous.Appearance, normalizedValue)
            };
            SettingsLockedSaveResult save = await _settingsStore.TrySaveAsync(
                updated,
                previous.Revision,
                cancellationToken).ConfigureAwait(false);
            if (!save.Succeeded)
            {
                SettingsMergeResult merge = SettingsThreeWayMerger.Merge(
                    previous,
                    save.CurrentSettings,
                    updated);
                if (!merge.Succeeded || merge.Settings is null)
                {
                    return new SettingsCommitResult(
                        false,
                        save.CurrentSettings.Revision,
                        currentState.Generation,
                        _localizer.Format(
                            "Settings_FieldPatch_Conflict",
                            fieldDisplayName))
                    {
                        CurrentSettings = save.CurrentSettings
                    };
                }

                SettingsChangeSet mergedChanges = SettingsChangeClassifier.Classify(
                    previous,
                    merge.Settings);
                if (mergedChanges.RequiresCatalogReload)
                {
                    SettingsPrepareResult preparation = await PrepareUpdateCoreAsync(
                        merge.Settings,
                        targetRevision: null,
                        cancellationToken: cancellationToken,
                        diskBaseRevision: save.CurrentSettings.Revision).ConfigureAwait(false);
                    if (!preparation.Succeeded || preparation.Plan is null)
                    {
                        return new SettingsCommitResult(
                            false,
                            save.CurrentSettings.Revision,
                            currentState.Generation,
                            string.Join(
                                System.Environment.NewLine,
                                preparation.Errors.Select(error => error.Message)))
                        {
                            CurrentSettings = save.CurrentSettings
                        };
                    }

                    return await CommitPreparedAsync(
                        preparation.Plan,
                        persistSettings: true,
                        cancellationToken).ConfigureAwait(false);
                }

                updated = merge.Settings with
                {
                    Revision = save.CurrentSettings.Revision + 1
                };
                save = await _settingsStore.TrySaveAsync(
                    updated,
                    save.CurrentSettings.Revision,
                    cancellationToken).ConfigureAwait(false);
                if (!save.Succeeded)
                {
                    return new SettingsCommitResult(
                        false,
                        save.CurrentSettings.Revision,
                        currentState.Generation,
                        save.Error)
                    {
                        CurrentSettings = save.CurrentSettings
                    };
                }
            }

            CatalogState committed;
            lock (_stateSync)
            {
                if (_state.Settings.Revision != previous.Revision)
                {
                    DictionaryCatalogSnapshot stale = _state.ToPublicSnapshot();
                    return new SettingsCommitResult(
                        false,
                        stale.SettingsRevision,
                        stale.Generation,
                        _localizer.Format(
                            "Settings_FieldPatch_RuntimeChanged",
                            fieldDisplayName));
                }

                foreach (DictionaryDefinition dictionary in
                         updated.Dictionaries.Where(item => item.Enabled))
                {
                    if (_state.Stores.TryGetValue(
                            dictionary.Id,
                            out DictionaryStore? store))
                    {
                        store.ApplyPresentation(dictionary);
                    }
                }

                committed = new CatalogState(updated, _state.Generation, _state.Stores);
                _state = committed;
            }

            DictionaryCatalogSnapshot snapshot = committed.ToPublicSnapshot();
            PublishSettingsCommitted(
                snapshot,
                SettingsChangeClassifier.Classify(previous, updated));
            return new SettingsCommitResult(
                true,
                snapshot.SettingsRevision,
                snapshot.Generation,
                null);
        }
        finally
        {
            _settingsCommitGate.Release();
        }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<DictionaryStore> stores;
        lock (_stateSync)
        {
            stores = _state.Stores.Values.Distinct().ToImmutableArray();
            _state = new CatalogState(
                _state.Settings,
                _state.Generation + 1,
                ImmutableDictionary<DictionaryId, DictionaryStore>.Empty);
        }
        _memoryCache.AdvanceGeneration(GetSnapshot().Generation, []);

        foreach (DictionaryStore store in stores)
        {
            // Once the public snapshot has been emptied, retirement is a mandatory
            // transaction tail and must not be interrupted halfway through.
            await store.RetireAsync().ConfigureAwait(false);
        }
    }

    public async Task<SettingsCommitResult> RebuildIndexesAsync(
        CancellationToken cancellationToken = default,
        IProgress<SettingsOperationProgress>? progress = null)
        => await RebuildIndexesCoreAsync(
            storesAlreadyReleased: false,
            cancellationToken,
            progress).ConfigureAwait(false);

    internal async Task<SettingsCommitResult> RebuildReleasedIndexesAsync(
        CancellationToken cancellationToken = default,
        IProgress<SettingsOperationProgress>? progress = null)
        => await RebuildIndexesCoreAsync(
            storesAlreadyReleased: true,
            cancellationToken,
            progress).ConfigureAwait(false);

    private async Task<SettingsCommitResult> RebuildIndexesCoreAsync(
        bool storesAlreadyReleased,
        CancellationToken cancellationToken,
        IProgress<SettingsOperationProgress>? progress)
    {
        ApplicationSettings settings = GetSettingsSnapshot();
        bool restoreRequired = storesAlreadyReleased;
        try
        {
            SettingsPrepareResult preparation = await PrepareUpdateCoreAsync(
                settings,
                settings.Revision,
                cancellationToken,
                progress,
                forceIndexRebuild: true).ConfigureAwait(false);
            if (!preparation.Succeeded || preparation.Plan is null)
            {
                string preparationError = string.Join(
                    System.Environment.NewLine,
                    preparation.Errors.Select(error => error.Message));
                string? preparationRestoreError = restoreRequired
                    ? await TryRestoreReleasedSettingsAsync(settings).ConfigureAwait(false)
                    : null;
                restoreRequired = false;
                return new SettingsCommitResult(
                    false,
                    settings.Revision,
                    GetSnapshot().Generation,
                    CombineMaintenanceErrors(
                        preparationError,
                        preparationRestoreError));
            }

            if (!storesAlreadyReleased)
            {
                await ReleaseAsync(cancellationToken).ConfigureAwait(false);
                restoreRequired = true;
            }

            SettingsCommitResult commit = await CommitPreparedAsync(
                preparation.Plan,
                persistSettings: false,
                cancellationToken).ConfigureAwait(false);
            if (commit.Succeeded)
            {
                restoreRequired = false;
                return commit;
            }

            string? restoreError = await TryRestoreReleasedSettingsAsync(settings)
                .ConfigureAwait(false);
            restoreRequired = false;
            return commit with
            {
                Error = CombineMaintenanceErrors(commit.Error, restoreError)
            };
        }
        catch (OperationCanceledException)
        {
            if (restoreRequired)
            {
                _ = await TryRestoreReleasedSettingsAsync(settings).ConfigureAwait(false);
                restoreRequired = false;
            }

            throw;
        }
        catch (Exception exception)
        {
            string? restoreError = restoreRequired
                ? await TryRestoreReleasedSettingsAsync(settings).ConfigureAwait(false)
                : null;
            restoreRequired = false;
            DictionaryCatalogSnapshot snapshot = GetSnapshot();
            return new SettingsCommitResult(
                false,
                snapshot.SettingsRevision,
                snapshot.Generation,
                CombineMaintenanceErrors(exception.Message, restoreError));
        }
        finally
        {
            if (restoreRequired)
            {
                _ = await TryRestoreReleasedSettingsAsync(settings).ConfigureAwait(false);
            }
        }
    }

    internal async Task<SettingsCommitResult> RestoreReleasedStoresAsync(
        CancellationToken cancellationToken = default,
        IProgress<SettingsOperationProgress>? progress = null)
    {
        ApplicationSettings settings = GetSettingsSnapshot();
        SettingsPrepareResult preparation = await PrepareUpdateCoreAsync(
            settings,
            settings.Revision,
            cancellationToken,
            progress).ConfigureAwait(false);
        if (!preparation.Succeeded || preparation.Plan is null)
        {
            return new SettingsCommitResult(
                false,
                settings.Revision,
                GetSnapshot().Generation,
                string.Join(
                    System.Environment.NewLine,
                    preparation.Errors.Select(error => error.Message)));
        }

        return await CommitPreparedAsync(
            preparation.Plan,
            persistSettings: false,
            cancellationToken,
            publishSettingsCommitted: false).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await ReleaseAsync().ConfigureAwait(false);
    }

    private async Task<SettingsCommitResult> CommitPreparedAsync(
        SettingsCommitPlan plan,
        bool persistSettings,
        CancellationToken cancellationToken,
        bool publishSettingsCommitted = true)
    {
        if (plan.ImplementationToken is not PreparedCatalogUpdate update ||
            !ReferenceEquals(update.Owner, _planOwner))
        {
            return new SettingsCommitResult(
                false,
                0,
                0,
                _localizer.Get("Settings_Commit_InvalidPlan"));
        }

        bool stale;
        CatalogState baseState;
        lock (_stateSync)
        {
            baseState = _state;
            stale = _state.Settings.Revision != plan.BaseRevision;
        }

        if (stale)
        {
            Cleanup(update.StagedIndexes, update.StagingDirectory);
            DictionaryCatalogSnapshot current = GetSnapshot();
            return new SettingsCommitResult(
                false,
                current.SettingsRevision,
                current.Generation,
                _localizer.Get("Settings_Commit_RuntimeChanged"));
        }

        Dictionary<DictionaryId, DictionaryStore> newStores = [];
        List<DictionaryStore> openedStores = [];
        try
        {
            SettingsChangeSet changes = SettingsChangeClassifier.Classify(
                baseState.Settings,
                update.Settings);
            if (!changes.RequiresCatalogReload && !update.ForceCatalogReload)
            {
                if (persistSettings)
                {
                    SettingsLockedSaveResult save =
                        await _settingsStore.TrySaveAsync(
                        update.Settings,
                        plan.DiskBaseRevision ?? plan.BaseRevision,
                        cancellationToken).ConfigureAwait(false);
                    if (!save.Succeeded)
                    {
                        DictionaryCatalogSnapshot current = GetSnapshot();
                        return new SettingsCommitResult(
                            false,
                            save.CurrentSettings.Revision,
                            current.Generation,
                            save.Error)
                        {
                            CurrentSettings = save.CurrentSettings
                        };
                    }
                }

                CatalogState lightweightState;
                lock (_stateSync)
                {
                    if (_state.Settings.Revision != plan.BaseRevision)
                    {
                        DictionaryCatalogSnapshot current = _state.ToPublicSnapshot();
                        return new SettingsCommitResult(
                            false,
                            current.SettingsRevision,
                            current.Generation,
                            _localizer.Get("Settings_Commit_RuntimeChanged"));
                    }

                    foreach (DictionaryDefinition dictionary in
                             update.Settings.Dictionaries.Where(item => item.Enabled))
                    {
                        if (_state.Stores.TryGetValue(
                                dictionary.Id,
                                out DictionaryStore? store))
                        {
                            store.ApplyPresentation(dictionary);
                        }
                    }

                    lightweightState = new CatalogState(
                        update.Settings,
                        _state.Generation,
                        _state.Stores);
                    _state = lightweightState;
                }

                DictionaryCatalogSnapshot lightweightSnapshot =
                    lightweightState.ToPublicSnapshot();
                if (publishSettingsCommitted)
                {
                    PublishSettingsCommitted(lightweightSnapshot, changes);
                }

                return new SettingsCommitResult(
                    true,
                    lightweightSnapshot.SettingsRevision,
                    lightweightSnapshot.Generation,
                    null);
            }

            foreach (StagedDictionaryIndex staged in update.StagedIndexes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _indexBuilder.Promote(staged, _paths.IndexesDirectory);
            }

            foreach (DictionaryDefinition dictionary in update.Settings.Dictionaries.Where(item => item.Enabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DictionaryIndexPath.TryGet(
                        _paths.IndexesDirectory,
                        dictionary.MdxPath,
                        out string indexPath))
                {
                    throw new InvalidDataException(
                        _localizer.Format(
                            "Catalog_IndexNotFound",
                            dictionary.EffectiveDisplayName));
                }

                if (baseState.Stores.TryGetValue(
                        dictionary.Id,
                        out DictionaryStore? existing) &&
                    existing.CanReuseFor(dictionary, indexPath))
                {
                    newStores[dictionary.Id] = existing;
                    continue;
                }

                DictionaryStore opened = await DictionaryStore.OpenAsync(
                    dictionary,
                    _paths.IndexesDirectory,
                    _paths.ResourcesDirectory,
                    _memoryCache,
                    cancellationToken).ConfigureAwait(false);
                openedStores.Add(opened);
                newStores[dictionary.Id] = opened;
            }

            if (persistSettings)
            {
                SettingsLockedSaveResult save = await _settingsStore.TrySaveAsync(
                    update.Settings,
                    plan.DiskBaseRevision ?? plan.BaseRevision,
                    cancellationToken).ConfigureAwait(false);
                if (!save.Succeeded)
                {
                    foreach (DictionaryStore store in openedStores)
                    {
                        await store.DisposeAsync().ConfigureAwait(false);
                    }

                    Cleanup(update.StagedIndexes, update.StagingDirectory);
                    DictionaryCatalogSnapshot current = GetSnapshot();
                    return new SettingsCommitResult(
                        false,
                        save.CurrentSettings.Revision,
                        current.Generation,
                        save.Error)
                    {
                        CurrentSettings = save.CurrentSettings
                    };
                }
            }

            CatalogState oldState;
            CatalogState newState;
            lock (_stateSync)
            {
                oldState = _state;
                newState = new CatalogState(
                    update.Settings,
                    oldState.Generation + 1,
                    newStores.ToImmutableDictionary());
                _state = newState;
            }
            _memoryCache.AdvanceGeneration(
                newState.Generation,
                newState.Stores.Keys);

            Cleanup([], update.StagingDirectory);
            DictionaryCatalogSnapshot snapshot = newState.ToPublicSnapshot();
            if (publishSettingsCommitted)
            {
                PublishSettingsCommitted(
                    snapshot,
                    SettingsChangeClassifier.Classify(oldState.Settings, newState.Settings));
            }
            _ = RetireStoresAsync(oldState.Stores.Values.Except(newState.Stores.Values).ToImmutableArray());
            return new SettingsCommitResult(
                true,
                update.Settings.Revision,
                newState.Generation,
                null);
        }
        catch (OperationCanceledException)
        {
            foreach (DictionaryStore store in openedStores)
            {
                await store.DisposeAsync().ConfigureAwait(false);
            }

            Cleanup(update.StagedIndexes, update.StagingDirectory);
            throw;
        }
        catch (Exception exception)
        {
            foreach (DictionaryStore store in openedStores)
            {
                await store.DisposeAsync().ConfigureAwait(false);
            }

            Cleanup(update.StagedIndexes, update.StagingDirectory);
            DictionaryCatalogSnapshot snapshot = GetSnapshot();
            return new SettingsCommitResult(
                false,
                snapshot.SettingsRevision,
                snapshot.Generation,
                exception.Message);
        }
    }

    private (long Generation, List<DictionaryStore.StoreLease> Leases) CaptureLeases(
        DictionaryId? selectedDictionaryId)
    {
        lock (_stateSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IEnumerable<KeyValuePair<DictionaryId, DictionaryStore>> stores = _state.Stores;
            if (selectedDictionaryId is { } selected)
            {
                stores = stores.Where(item => item.Key == selected);
            }

            return (
                _state.Generation,
                stores.Select(item => item.Value.AcquireLease()).ToList());
        }
    }

    private static ImmutableArray<string> MergeSuggestions<TCollection>(
        IEnumerable<TCollection> perStore,
        int limit,
        ImmutableArray<string> existing)
        where TCollection : IEnumerable<string>
    {
        ImmutableArray<string>.Builder output = existing.ToBuilder();
        HashSet<string> seen = new(existing, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> ordered = perStore
            .SelectMany((words, storeIndex) =>
                words.Select((word, itemIndex) => new { Word = word, StoreIndex = storeIndex, ItemIndex = itemIndex }))
            .OrderBy(item => item.Word, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.StoreIndex)
            .ThenBy(item => item.ItemIndex)
            .Select(item => item.Word);
        foreach (string word in ordered)
        {
            if (!seen.Add(word))
            {
                continue;
            }

            output.Add(word);
            if (output.Count >= limit)
            {
                break;
            }
        }

        return output.ToImmutable();
    }

    private async Task ValidateFilesAndDuplicatesAsync(
        ApplicationSettings settings,
        ImmutableArray<DictionaryPreparationError>.Builder errors,
        CancellationToken cancellationToken,
        IProgress<SettingsOperationProgress>? progress)
    {
        Dictionary<string, DictionaryDefinition> paths = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<(long Length, string Hash), DictionaryDefinition> hashes = [];
        for (int index = 0; index < settings.Dictionaries.Length; index++)
        {
            DictionaryDefinition dictionary = settings.Dictionaries[index];
            cancellationToken.ThrowIfCancellationRequested();
            if (!dictionary.Enabled)
            {
                continue;
            }

            progress?.Report(new SettingsOperationProgress(
                "validation",
                _localizer.Format(
                    "Settings_Progress_CheckingFiles",
                    dictionary.EffectiveDisplayName),
                index,
                settings.Dictionaries.Length,
                dictionary.Id,
                dictionary.EffectiveDisplayName));
            if (!File.Exists(dictionary.MdxPath) ||
                !Path.GetExtension(dictionary.MdxPath).Equals(".mdx", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new(
                    dictionary.Id,
                    _localizer.Get("Validation_MdxFile")));
                continue;
            }

            string fullPath = Path.GetFullPath(dictionary.MdxPath);
            if (paths.TryGetValue(fullPath, out DictionaryDefinition? duplicatePath))
            {
                errors.Add(new(
                    dictionary.Id,
                    _localizer.Format(
                        "Validation_MdxPathDuplicate",
                        duplicatePath.EffectiveDisplayName)));
                continue;
            }

            paths[fullPath] = dictionary;
            foreach (string mddPath in dictionary.MddPaths)
            {
                if (!File.Exists(mddPath) ||
                    !Path.GetExtension(mddPath).Equals(".mdd", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new(
                        dictionary.Id,
                        _localizer.Format("Validation_MddFile", mddPath)));
                }
            }

            foreach (string cssPath in dictionary.CssPaths)
            {
                if (!File.Exists(cssPath) ||
                    !Path.GetExtension(cssPath).Equals(".css", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new(
                        dictionary.Id,
                        _localizer.Format("Validation_CssFile", cssPath)));
                }
            }

            foreach (string scriptPath in dictionary.ScriptPaths)
            {
                if (!File.Exists(scriptPath) ||
                    !Path.GetExtension(scriptPath).Equals(".js", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new(
                        dictionary.Id,
                        _localizer.Format("Validation_JavaScriptFile", scriptPath)));
                }
            }

            FileInfo info = new(fullPath);
            long originalLength = info.Length;
            long originalLastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
            FileHashStamp stamp = new(
                fullPath,
                originalLength,
                originalLastWriteUtcTicks);
            if (!FileHashCache.TryGetValue(stamp, out string? hash))
            {
                await using FileStream stream = new(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                info.Refresh();
                if (!info.Exists ||
                    stream.Length != originalLength ||
                    info.Length != originalLength ||
                    info.LastWriteTimeUtc.Ticks != originalLastWriteUtcTicks)
                {
                    errors.Add(new(
                        dictionary.Id,
                        _localizer.Get("Validation_MdxChangedDuringHash")));
                    continue;
                }

                FileHashCache[stamp] = hash;
            }

            if (hashes.TryGetValue((originalLength, hash), out DictionaryDefinition? duplicateContent))
            {
                errors.Add(new(
                    dictionary.Id,
                    _localizer.Format(
                        "Validation_MdxContentDuplicate",
                        duplicateContent.EffectiveDisplayName)));
            }
            else
            {
                hashes[(originalLength, hash)] = dictionary;
            }
        }

        progress?.Report(new SettingsOperationProgress(
            "validation",
            _localizer.Get("Settings_Progress_FilesValidated"),
            settings.Dictionaries.Length,
            settings.Dictionaries.Length));
    }

    private void PublishSettingsCommitted(
        DictionaryCatalogSnapshot snapshot,
        SettingsChangeSet changes)
    {
        Delegate[] handlers = SettingsCommitted?.GetInvocationList() ?? [];
        SettingsCommittedEvent committed = new(snapshot, changes);
        foreach (EventHandler<SettingsCommittedEvent> handler in handlers
                     .OfType<EventHandler<SettingsCommittedEvent>>())
        {
            try
            {
                handler(this, committed);
            }
            catch
            {
                // A presentation subscriber cannot roll back an already committed transaction.
            }
        }
    }

    private async Task RestoreReleasedSettingsAsync(ApplicationSettings settings)
    {
        SettingsPrepareResult recovery = await PrepareUpdateCoreAsync(
            settings,
            settings.Revision,
            CancellationToken.None).ConfigureAwait(false);
        if (!recovery.Succeeded || recovery.Plan is null)
        {
            throw new InvalidOperationException(
                string.Join(
                    System.Environment.NewLine,
                    recovery.Errors.Select(error => error.Message)));
        }

        SettingsCommitResult commit = await CommitPreparedAsync(
            recovery.Plan,
            persistSettings: false,
            CancellationToken.None,
            publishSettingsCommitted: false).ConfigureAwait(false);
        if (!commit.Succeeded)
        {
            throw new InvalidOperationException(commit.Error);
        }
    }

    private async Task<string?> TryRestoreReleasedSettingsAsync(ApplicationSettings settings)
    {
        try
        {
            await RestoreReleasedSettingsAsync(settings).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            _ = exception;
            return _localizer.Get("Maintenance_RestoreDictionaryStateFailed");
        }
    }

    private static string? CombineMaintenanceErrors(string? operationError, string? restoreError)
    {
        if (string.IsNullOrWhiteSpace(operationError))
        {
            return restoreError;
        }

        return string.IsNullOrWhiteSpace(restoreError)
            ? operationError
            : operationError + System.Environment.NewLine + restoreError;
    }

    private static async Task RetireStoresAsync(ImmutableArray<DictionaryStore> stores)
    {
        foreach (DictionaryStore store in stores.Distinct())
        {
            try
            {
                await store.RetireAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static void Cleanup(
        IEnumerable<StagedDictionaryIndex> staged,
        string stagingDirectory)
    {
        foreach (StagedDictionaryIndex item in staged)
        {
            DictionaryIndexBuilder.Cleanup(item);
        }

        try
        {
            if (Directory.Exists(stagingDirectory) &&
                !Directory.EnumerateFileSystemEntries(stagingDirectory).Any())
            {
                Directory.Delete(stagingDirectory);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PreparedCatalogUpdate(
        object Owner,
        ApplicationSettings BaseSettings,
        ApplicationSettings Settings,
        ImmutableArray<StagedDictionaryIndex> StagedIndexes,
        string StagingDirectory,
        bool ForceCatalogReload);

    private readonly record struct FileHashStamp(
        string FullPath,
        long Length,
        long LastWriteUtcTicks);

    private sealed record CatalogState(
        ApplicationSettings Settings,
        long Generation,
        ImmutableDictionary<DictionaryId, DictionaryStore> Stores)
    {
        public static CatalogState Empty { get; } = new(
            ApplicationSettings.Default,
            0,
            ImmutableDictionary<DictionaryId, DictionaryStore>.Empty);

        public DictionaryCatalogSnapshot ToPublicSnapshot()
        {
            ImmutableArray<DictionaryDefinition> active = Settings.Dictionaries
                .Where(dictionary => dictionary.Enabled && Stores.ContainsKey(dictionary.Id))
                .ToImmutableArray();
            ImmutableArray<CatalogStoreHandle> handles = active
                .Select((dictionary, index) => new CatalogStoreHandle(dictionary.Id, index + 1L))
                .ToImmutableArray();
            return new DictionaryCatalogSnapshot(
                Generation,
                Settings.Revision,
                active,
                handles);
        }
    }

    private sealed class LeaseCollection(IEnumerable<DictionaryStore.StoreLease> leases) : IDisposable
    {
        private readonly List<DictionaryStore.StoreLease> _leases = leases.ToList();

        public void Dispose()
        {
            foreach (DictionaryStore.StoreLease lease in _leases)
            {
                lease.Dispose();
            }
        }
    }
}
