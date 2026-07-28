using System.Collections.Immutable;
using VDict.Core.Catalog;
using VDict.Core.Models;

namespace VDict.Presentation.Tests.TestDoubles;

internal sealed class FakeCatalog : IDictionaryCatalog
{
    public long Generation { get; set; }

    public long Revision { get; set; }

    public ApplicationSettings Settings { get; private set; } = ApplicationSettings.Default;

    public ImmutableArray<DictionaryDefinition> ActiveDictionaries { get; set; } =
        [CreateDictionary("dict")];

    public List<string> SuggestedQueries { get; } = [];

    public List<string> LookedUpQueries { get; } = [];

    public List<LookupQuery> SuggestedRequests { get; } = [];

    public List<LookupQuery> LookupRequests { get; } = [];

    public int CanceledSuggestions { get; private set; }

    public Func<LookupQuery, CancellationToken, Task<SuggestionResult>>? SuggestionHandler { get; set; }

    public Func<LookupQuery, CancellationToken, Task<ImmutableArray<LookupResult>>>? LookupHandler { get; set; }

    public Func<LookupQuery, CancellationToken, Task<LookupBatchResult>>? LookupBatchHandler { get; set; }

    public Func<ApplicationSettings, CancellationToken, Task<SettingsPrepareResult>>? PrepareHandler { get; set; }

    public Func<SettingsCommitPlan, CancellationToken, Task<SettingsCommitResult>>? CommitHandler { get; set; }

    public Func<DictionaryId, string, CancellationToken, Task<ResourceResolution>>? ResourceHandler { get; set; }

    public int ResourceRequests { get; private set; }

    public event EventHandler<SettingsCommittedEvent>? SettingsCommitted;

    public DictionaryCatalogSnapshot GetSnapshot() =>
        new(Generation, Revision, ActiveDictionaries, []);

    public ApplicationSettings GetSettingsSnapshot() =>
        Settings with { Revision = Revision };

    public async Task<SuggestionResult> SuggestAsync(
        LookupQuery query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        SuggestedQueries.Add(query.Text);
        SuggestedRequests.Add(query);
        try
        {
            return SuggestionHandler is null
                ? new SuggestionResult(query.Text, query.SelectedDictionaryId, Generation, [query.Text])
                : await SuggestionHandler(query, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CanceledSuggestions++;
            throw;
        }
    }

    public Task<ImmutableArray<LookupResult>> LookupAsync(
        LookupQuery query,
        CancellationToken cancellationToken = default)
    {
        LookedUpQueries.Add(query.Text);
        LookupRequests.Add(query);
        return LookupHandler?.Invoke(query, cancellationToken) ??
               Task.FromResult(ImmutableArray<LookupResult>.Empty);
    }

    public async Task<LookupBatchResult> LookupBatchAsync(
        LookupQuery query,
        CancellationToken cancellationToken = default)
    {
        if (LookupBatchHandler is not null)
        {
            LookedUpQueries.Add(query.Text);
            return await LookupBatchHandler(query, cancellationToken);
        }

        return new LookupBatchResult(await LookupAsync(query, cancellationToken), []);
    }

    public Task<ResourceResolution> ResolveResourceAsync(
        DictionaryId dictionaryId,
        string path,
        CancellationToken cancellationToken = default)
    {
        ResourceRequests++;
        return ResourceHandler?.Invoke(dictionaryId, path, cancellationToken) ??
               Task.FromResult(new ResourceResolution(null, path, "none", "missing"));
    }

    public Task<SettingsPrepareResult> PrepareUpdateAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken = default,
        IProgress<SettingsOperationProgress>? progress = null)
    {
        progress?.Report(new SettingsOperationProgress("prepare", "prepared", 1, 1));
        if (PrepareHandler is not null)
        {
            return PrepareHandler(settings, cancellationToken);
        }

        SettingsCommitPlan plan = new(settings with { Revision = Revision + 1 }, Revision, [], new object());
        return Task.FromResult(new SettingsPrepareResult(plan, []));
    }

    public Task<SettingsCommitResult> CommitUpdateAsync(
        SettingsCommitPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (CommitHandler is not null)
        {
            return CommitHandler(plan, cancellationToken);
        }

        ApplicationSettings previous = Settings;
        SettingsChangeSet changes = VDict.Core.Settings.SettingsChangeClassifier.Classify(
            previous,
            plan.Settings);
        Revision = plan.Settings.Revision;
        Settings = plan.Settings;
        if (changes.RequiresCatalogReload)
        {
            Generation++;
        }
        DictionaryCatalogSnapshot snapshot = GetSnapshot();
        SettingsCommitted?.Invoke(this, new SettingsCommittedEvent(snapshot, changes));
        return Task.FromResult(new SettingsCommitResult(true, Revision, Generation, null));
    }

    public Task<SettingsCommitResult> UpdateZoomAsync(
        double zoom,
        long baseRevision,
        double baseZoom,
        CancellationToken cancellationToken = default)
    {
        if (Revision != baseRevision &&
            Math.Abs(Settings.Appearance.ContentZoom - baseZoom) >= 0.0001)
        {
            return Task.FromResult(
                new SettingsCommitResult(true, Revision, Generation, null));
        }

        Revision++;
        Settings = Settings with
        {
            Revision = Revision,
            Appearance = Settings.Appearance with { ContentZoom = zoom }
        };
        DictionaryCatalogSnapshot snapshot = GetSnapshot();
        SettingsCommitted?.Invoke(
            this,
            new SettingsCommittedEvent(
                snapshot,
                new SettingsChangeSet(SettingsChangeKind.Zoom)));
        return Task.FromResult(
            new SettingsCommitResult(true, Revision, Generation, null));
    }

    public Task<SettingsCommitResult> UpdateCandidatePaneWidthAsync(
        double width,
        long baseRevision,
        double? baseWidth,
        CancellationToken cancellationToken = default)
    {
        if (Revision != baseRevision &&
            Settings.Appearance.CandidatePaneWidth != baseWidth)
        {
            return Task.FromResult(
                new SettingsCommitResult(true, Revision, Generation, null));
        }

        Revision++;
        Settings = Settings with
        {
            Revision = Revision,
            Appearance = Settings.Appearance with { CandidatePaneWidth = width }
        };
        DictionaryCatalogSnapshot snapshot = GetSnapshot();
        SettingsCommitted?.Invoke(
            this,
            new SettingsCommittedEvent(
                snapshot,
                new SettingsChangeSet(SettingsChangeKind.MainWindowLayout)));
        return Task.FromResult(
            new SettingsCommitResult(true, Revision, Generation, null));
    }

    public Task ReleaseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private static DictionaryDefinition CreateDictionary(string id) => new(
        new DictionaryId(id),
        id,
        null,
        $"{id}.mdx",
        true,
        true,
        [],
        [],
        DictionaryAppearance.Default);
}
