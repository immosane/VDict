using System.Collections.Immutable;
using VDict.Core.Models;

namespace VDict.Core.Catalog;

public sealed record CatalogStoreHandle(DictionaryId DictionaryId, long LeaseId);

public sealed record DictionaryCatalogSnapshot(
    long Generation,
    long SettingsRevision,
    ImmutableArray<DictionaryDefinition> ActiveDictionaries,
    ImmutableArray<CatalogStoreHandle> StoreHandles);

public sealed record SettingsCommittedEvent(
    DictionaryCatalogSnapshot Snapshot,
    SettingsChangeSet Changes);

public interface IDictionaryCatalog
{
    DictionaryCatalogSnapshot GetSnapshot();

    ApplicationSettings GetSettingsSnapshot();

    Task<SuggestionResult> SuggestAsync(
        LookupQuery query,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ImmutableArray<LookupResult>> LookupAsync(
        LookupQuery query,
        CancellationToken cancellationToken = default);

    async Task<LookupBatchResult> LookupBatchAsync(
        LookupQuery query,
        CancellationToken cancellationToken = default) =>
        new(await LookupAsync(query, cancellationToken), []);

    Task<ResourceResolution> ResolveResourceAsync(
        DictionaryId dictionaryId,
        string path,
        CancellationToken cancellationToken = default);

    Task<SettingsPrepareResult> PrepareUpdateAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken = default,
        IProgress<SettingsOperationProgress>? progress = null);

    Task<SettingsCommitResult> CommitUpdateAsync(
        SettingsCommitPlan plan,
        CancellationToken cancellationToken = default);

    Task<SettingsCommitResult> UpdateZoomAsync(
        double zoom,
        long baseRevision,
        double baseZoom,
        CancellationToken cancellationToken = default);

    Task<SettingsCommitResult> UpdateCandidatePaneWidthAsync(
        double width,
        long baseRevision,
        double? baseWidth,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(CancellationToken cancellationToken = default);

    event EventHandler<SettingsCommittedEvent>? SettingsCommitted;
}
