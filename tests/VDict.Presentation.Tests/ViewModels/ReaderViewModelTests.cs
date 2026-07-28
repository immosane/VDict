using System.Collections.Immutable;
using System.Collections.Specialized;
using VDict.Core.Environment;
using VDict.Core.Models;
using VDict.Presentation.Services;
using VDict.Presentation.Tests.TestDoubles;
using VDict.Presentation.ViewModels;

namespace VDict.Presentation.Tests.ViewModels;

public sealed class ReaderViewModelTests
{
    [Fact]
    public void ReadyCatalog_SelectsFirstEnabledDictionaryInsteadOfAll()
    {
        FakeCatalog catalog = new()
        {
            ActiveDictionaries = [Dictionary("first"), Dictionary("second")]
        };
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());

        viewModel.MarkCatalogReady();

        Assert.Equal(new DictionaryId("first"), viewModel.SelectedDictionaryId);
    }

    [Fact]
    public void Reload_PreservesEnabledSelectionOrFallsBackToFirst()
    {
        FakeCatalog catalog = new()
        {
            ActiveDictionaries = [Dictionary("first"), Dictionary("second")]
        };
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();
        viewModel.SelectedDictionaryId = new DictionaryId("second");

        catalog.ActiveDictionaries = [Dictionary("second"), Dictionary("first")];
        viewModel.RefreshDictionarySelection();
        Assert.Equal(new DictionaryId("second"), viewModel.SelectedDictionaryId);

        catalog.ActiveDictionaries = [Dictionary("replacement")];
        viewModel.RefreshDictionarySelection();
        Assert.Equal(new DictionaryId("replacement"), viewModel.SelectedDictionaryId);
    }

    [Fact]
    public async Task SuggestionsAndLookup_AreScopedToSelectedDictionary()
    {
        FakeCatalog catalog = new()
        {
            ActiveDictionaries = [Dictionary("first")]
        };
        using ReaderViewModel viewModel = new(
            catalog,
            new ImmediateUiDispatcher(),
            TimeSpan.Zero);
        viewModel.MarkCatalogReady();

        viewModel.SearchText = "word";
        await WaitUntilAsync(() => catalog.SuggestedRequests.Count > 0);
        await viewModel.LookupAsync("word");

        Assert.All(
            catalog.SuggestedRequests,
            request => Assert.Equal(new DictionaryId("first"), request.SelectedDictionaryId));
        Assert.Equal(
            new DictionaryId("first"),
            Assert.Single(catalog.LookupRequests).SelectedDictionaryId);
    }

    [Fact]
    public async Task NoEnabledDictionary_UsesExplicitEmptyStateAndDoesNotQuery()
    {
        FakeCatalog catalog = new() { ActiveDictionaries = [] };
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();

        await viewModel.LookupAsync("word");

        Assert.Null(viewModel.SelectedDictionaryId);
        Assert.Empty(catalog.LookupRequests);
        Assert.Contains("no-dictionaries", viewModel.Document!.Html);
    }

    [Fact]
    public void InitializingCatalog_DisablesLookupAndDictionarySelection()
    {
        using ReaderViewModel viewModel = new(new FakeCatalog(), new ImmediateUiDispatcher())
        {
            SearchText = "word"
        };

        Assert.Equal(CatalogLifecycleState.Initializing, viewModel.CatalogState);
        Assert.False(viewModel.LookupCommand.CanExecute(null));
        Assert.False(viewModel.CanSelectDictionary);

        viewModel.MarkCatalogReady();

        Assert.Equal(CatalogLifecycleState.Ready, viewModel.CatalogState);
        Assert.True(viewModel.LookupCommand.CanExecute(null));
        Assert.True(viewModel.CanSelectDictionary);
    }

    [Fact]
    public async Task Candidates_AreDebouncedAndOnlyLatestInputIsApplied()
    {
        FakeCatalog catalog = new();
        using ReaderViewModel viewModel = new(
            catalog,
            new ImmediateUiDispatcher(),
            TimeSpan.FromMilliseconds(20));
        viewModel.MarkCatalogReady();

        viewModel.SearchText = "a";
        await Task.Delay(5);
        viewModel.SearchText = "ab";
        await WaitUntilAsync(() => viewModel.Candidates.Count == 1);

        Assert.Contains("ab", catalog.SuggestedQueries);
        Assert.Equal("ab", catalog.SuggestedQueries[^1]);
        Assert.Equal("ab", viewModel.Candidates[0]);
    }

    [Fact]
    public async Task CandidateResult_IsPublishedAsOneCollectionReset()
    {
        FakeCatalog catalog = new();
        TaskCompletionSource<SuggestionResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.SuggestionHandler = (_, _) => completion.Task;
        using ReaderViewModel viewModel = new(
            catalog,
            new ImmediateUiDispatcher(),
            TimeSpan.Zero);
        viewModel.MarkCatalogReady();
        List<NotifyCollectionChangedAction> actions = [];
        viewModel.Candidates.CollectionChanged += (_, args) => actions.Add(args.Action);

        viewModel.SearchText = "word";
        await WaitUntilAsync(() => catalog.SuggestedQueries.Count == 1);
        actions.Clear();
        completion.SetResult(new SuggestionResult("word", null, 0, ["one", "two", "three"]));
        await WaitUntilAsync(() => viewModel.Candidates.Count == 3);

        Assert.Equal([NotifyCollectionChangedAction.Reset], actions);
        Assert.Equal(["one", "two", "three"], viewModel.Candidates);
    }

    [Fact]
    public async Task CandidateLookup_PreservesPublishedListAndDoesNotRequestSuggestionsAgain()
    {
        FakeCatalog catalog = new()
        {
            SuggestionHandler = (query, _) => Task.FromResult(
                new SuggestionResult(
                    query.Text,
                    query.SelectedDictionaryId,
                    0,
                    ["candidate", "candidate-two"]))
        };
        using ReaderViewModel viewModel = new(
            catalog,
            new ImmediateUiDispatcher(),
            TimeSpan.Zero);
        viewModel.MarkCatalogReady();
        viewModel.SearchText = "prefix";
        await WaitUntilAsync(() => viewModel.Candidates.Count == 2);
        string[] published = viewModel.Candidates.ToArray();
        int suggestionRequests = catalog.SuggestedRequests.Count;
        List<NotifyCollectionChangedAction> actions = [];
        viewModel.Candidates.CollectionChanged += (_, args) => actions.Add(args.Action);

        await viewModel.LookupCandidateAsync(published[0]);

        Assert.Equal(published[0], viewModel.SearchText);
        Assert.Equal(published, viewModel.Candidates);
        Assert.Equal(suggestionRequests, catalog.SuggestedRequests.Count);
        Assert.Empty(actions);
    }

    [Fact]
    public void DefaultCandidateDebounce_IsEightyMilliseconds()
    {
        using ReaderViewModel viewModel = new(new FakeCatalog(), new ImmediateUiDispatcher());
        System.Reflection.FieldInfo field = typeof(ReaderViewModel).GetField(
            "_candidateDebounce",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        Assert.Equal(TimeSpan.FromMilliseconds(80), field.GetValue(viewModel));
    }

    [Fact]
    public async Task InputChange_ClearsStaleCandidatesBeforeDebounce()
    {
        FakeCatalog catalog = new();
        using ReaderViewModel viewModel = new(
            catalog,
            new ImmediateUiDispatcher(),
            TimeSpan.FromSeconds(1));
        viewModel.MarkCatalogReady();
        viewModel.Candidates.Add("stale");

        viewModel.SearchText = "new";

        Assert.Empty(viewModel.Candidates);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CandidatePanel_ExposesEmptyAndNoMatchStates()
    {
        FakeCatalog catalog = new()
        {
            SuggestionHandler = (query, _) => Task.FromResult(
                new SuggestionResult(query.Text, query.SelectedDictionaryId, 0, []))
        };
        using ReaderViewModel viewModel = new(
            catalog,
            new ImmediateUiDispatcher(),
            TimeSpan.Zero);
        viewModel.MarkCatalogReady();

        Assert.Equal("Reader_Candidates_EnterWord", viewModel.CandidateStateMessage);

        viewModel.SearchText = "missing";
        await WaitUntilAsync(() =>
            viewModel.CandidateStateMessage == "Reader_Candidates_NoMatch");

        Assert.Empty(viewModel.Candidates);
    }

    [Fact]
    public async Task Candidates_DropResultWhenCatalogGenerationChanges()
    {
        FakeCatalog catalog = new() { Generation = 1 };
        TaskCompletionSource<SuggestionResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.SuggestionHandler = (_, _) => completion.Task;
        using ReaderViewModel viewModel = new(
            catalog,
            new ImmediateUiDispatcher(),
            TimeSpan.Zero);
        viewModel.MarkCatalogReady();

        viewModel.SearchText = "word";
        await WaitUntilAsync(() => catalog.SuggestedQueries.Count == 1);
        catalog.Generation = 2;
        completion.SetResult(new SuggestionResult("word", null, 1, ["stale"]));
        await Task.Delay(30);

        Assert.Empty(viewModel.Candidates);
    }

    [Fact]
    public async Task StaleCandidateFailure_DoesNotOverwriteNewerCandidateState()
    {
        FakeCatalog catalog = new();
        TaskCompletionSource<SuggestionResult> stale =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.SuggestionHandler = (query, _) =>
            query.Text == "first"
                ? stale.Task
                : Task.FromResult(new SuggestionResult(
                    query.Text,
                    query.SelectedDictionaryId,
                    catalog.Generation,
                    ["second"]));
        using ReaderViewModel viewModel = new(
            catalog,
            new ImmediateUiDispatcher(),
            TimeSpan.Zero);
        viewModel.MarkCatalogReady();
        viewModel.SearchText = "first";
        await WaitUntilAsync(() => catalog.SuggestedQueries.Contains("first"));
        viewModel.SearchText = "second";
        await WaitUntilAsync(() => viewModel.Candidates.SequenceEqual(["second"]));

        stale.SetException(new IOException("stale failure"));
        await Task.Delay(30);

        Assert.Equal(["second"], viewModel.Candidates);
        Assert.DoesNotContain(
            "stale failure",
            viewModel.StatusMessage ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lookup_DropsResultsFromRetiredGeneration()
    {
        FakeCatalog catalog = new() { Generation = 4 };
        TaskCompletionSource<ImmutableArray<LookupResult>> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.LookupHandler = (_, _) => completion.Task;
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();
        ReaderDocument welcome = viewModel.Document!;

        Task lookup = viewModel.LookupAsync("word");
        catalog.Generation = 5;
        completion.SetResult(
            [new LookupResult(new DictionaryId("dict"), "Dictionary", "word", ["body"])]);
        await lookup;

        Assert.Same(welcome, viewModel.Document);
        Assert.Equal(string.Empty, viewModel.CurrentWord);
    }

    [Fact]
    public async Task NewLookup_CancelsPreviousLookup()
    {
        FakeCatalog catalog = new();
        int canceled = 0;
        catalog.LookupHandler = async (query, token) =>
        {
            if (query.Text == "first")
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref canceled);
                    throw;
                }
            }

            return ImmutableArray<LookupResult>.Empty;
        };
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();

        Task first = viewModel.LookupAsync("first");
        await Task.Delay(20);
        Task second = viewModel.LookupAsync("second");
        await Task.WhenAll(first, second);

        Assert.Equal(1, canceled);
        Assert.Equal("second", viewModel.CurrentWord);
    }

    [Fact]
    public async Task RapidLookups_RunOneAndRetainOnlyLatestPendingRequest()
    {
        FakeCatalog catalog = new();
        TaskCompletionSource firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> started = [];
        catalog.LookupHandler = async (query, _) =>
        {
            lock (started)
            {
                started.Add(query.Text);
            }

            if (query.Text == "word-0")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }

            return
            [
                new LookupResult(
                    new DictionaryId("dict"),
                    "Dictionary",
                    query.Text,
                    [$"<p>{query.Text}</p>"])
            ];
        };
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();

        List<Task> lookups = [viewModel.LookupAsync("word-0")];
        await firstStarted.Task;
        for (int index = 1; index < 10; index++)
        {
            lookups.Add(viewModel.LookupAsync($"word-{index}"));
        }

        releaseFirst.TrySetResult();
        await Task.WhenAll(lookups);

        Assert.Equal(["word-0", "word-9"], started);
        Assert.Equal(1, viewModel.DiagnosticBackgroundBuildConcurrencyPeak);
        Assert.Equal("word-9", viewModel.CurrentWord);
        Assert.Contains("word-9", viewModel.Document!.Html);
    }

    [Fact]
    public async Task LookupBusyState_AllowsImmediateReplacementQuery()
    {
        FakeCatalog catalog = new();
        TaskCompletionSource<ImmutableArray<LookupResult>> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.LookupHandler = (_, _) => completion.Task;
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher())
        {
            SearchText = "word"
        };
        viewModel.MarkCatalogReady();

        Task lookup = viewModel.LookupAsync("word");
        await WaitUntilAsync(() => viewModel.IsLookingUp);

        Assert.True(viewModel.LookupCommand.CanExecute(null));
        Assert.True(viewModel.CancelCommand.CanExecute(null));
        Assert.True(viewModel.CanSelectDictionary);

        completion.SetResult([]);
        await lookup;
        Assert.True(viewModel.LookupCommand.CanExecute(null));
        Assert.False(viewModel.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task BackForward_RestoresPathWithoutAppendingDestinationAgain()
    {
        FakeCatalog catalog = new();
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();

        await viewModel.LookupAsync("first");
        await viewModel.LookupAsync("second");
        await viewModel.BackCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "first" }, viewModel.LookupPath.ToArray());

        await viewModel.ForwardCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "first", "second" }, viewModel.LookupPath.ToArray());
    }

    [Fact]
    public async Task HistoryNavigation_CommitsLookupPathOnUiDispatcher()
    {
        FakeCatalog catalog = new();
        TrackingUiDispatcher dispatcher = new();
        using ReaderViewModel viewModel = new(catalog, dispatcher);
        viewModel.MarkCatalogReady();
        await viewModel.LookupAsync("first");
        await viewModel.LookupAsync("second");
        bool? changedOnUiDispatcher = null;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ReaderViewModel.LookupPath))
            {
                changedOnUiDispatcher = dispatcher.HasThreadAccess;
            }
        };

        await viewModel.BackCommand.ExecuteAsync(null);

        Assert.True(changedOnUiDispatcher);
        Assert.Equal(new[] { "first" }, viewModel.LookupPath.ToArray());
    }

    [Fact]
    public async Task FailedBackNavigation_DoesNotConsumeHistoryOrChangePath()
    {
        FakeCatalog catalog = new();
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();
        await viewModel.LookupAsync("first");
        await viewModel.LookupAsync("second");
        catalog.LookupHandler = (query, _) =>
            query.Text == "first"
                ? throw new IOException("lookup failed")
                : Task.FromResult(ImmutableArray<LookupResult>.Empty);

        await viewModel.BackCommand.ExecuteAsync(null);

        Assert.Equal("second", viewModel.CurrentWord);
        Assert.Equal(new[] { "first", "second" }, viewModel.LookupPath.ToArray());
        Assert.True(viewModel.BackCommand.CanExecute(null));
        Assert.False(viewModel.ForwardCommand.CanExecute(null));
    }

    [Fact]
    public async Task ManualQueryEdit_ResetsLookupPath()
    {
        FakeCatalog catalog = new();
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();
        await viewModel.LookupAsync("first");

        viewModel.SearchText = "manual";
        await viewModel.LookupAsync(viewModel.SearchText);

        Assert.Equal(new[] { "manual" }, viewModel.LookupPath.ToArray());
    }

    [Fact]
    public async Task EntryNavigation_AppendsInsteadOfResettingLookupPath()
    {
        FakeCatalog catalog = new();
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();
        await viewModel.LookupAsync("first");

        await viewModel.NavigateEntryAsync("linked");

        Assert.Equal(new[] { "first", "linked" }, viewModel.LookupPath.ToArray());
    }

    [Fact]
    public async Task CandidateNavigation_ReplacesLastLookupPathItem()
    {
        FakeCatalog catalog = new();
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();
        await viewModel.LookupAsync("first");
        await viewModel.NavigateEntryAsync("linked");

        await viewModel.LookupCandidateAsync("candidate");

        Assert.Equal(
            new[] { "first", "candidate" },
            viewModel.LookupPath.ToArray());
    }

    [Fact]
    public async Task SelectedTextNavigation_AppendsAndParticipatesInBackForwardHistory()
    {
        FakeCatalog catalog = new();
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();
        await viewModel.LookupAsync("first");

        await viewModel.NavigateSelectedTextAsync("selected");

        Assert.Equal(new[] { "first", "selected" }, viewModel.LookupPath.ToArray());
        await viewModel.BackCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "first" }, viewModel.LookupPath.ToArray());
        await viewModel.ForwardCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "first", "selected" }, viewModel.LookupPath.ToArray());
    }

    [Fact]
    public async Task PathNavigation_UsesRealIndexAndBackRestoresFullSnapshot()
    {
        FakeCatalog catalog = new();
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();
        await viewModel.LookupAsync("A");
        await viewModel.NavigateEntryAsync("B");
        await viewModel.NavigateEntryAsync("A");
        await viewModel.NavigateEntryAsync("C");

        await viewModel.NavigateLookupPathAsync(2);

        Assert.Equal(new[] { "A", "B", "A" }, viewModel.LookupPath.ToArray());
        Assert.Equal("A", viewModel.CurrentWord);
        await viewModel.BackCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "A", "B", "A", "C" }, viewModel.LookupPath.ToArray());
        Assert.Equal("C", viewModel.CurrentWord);
    }

    [Fact]
    public async Task FailedPathNavigation_DoesNotTruncatePathOrCreateHistory()
    {
        FakeCatalog catalog = new();
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();
        await viewModel.LookupAsync("A");
        await viewModel.NavigateEntryAsync("B");
        catalog.LookupHandler = (query, _) =>
            query.Text == "A"
                ? throw new IOException("lookup failed")
                : Task.FromResult(ImmutableArray<LookupResult>.Empty);

        await viewModel.NavigateLookupPathAsync(0);

        Assert.Equal("B", viewModel.CurrentWord);
        Assert.Equal(new[] { "A", "B" }, viewModel.LookupPath.ToArray());
    }

    [Fact]
    public async Task CurrentPathItem_DoesNotNavigateAgain()
    {
        FakeCatalog catalog = new();
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();
        await viewModel.LookupAsync("A");
        await viewModel.NavigateEntryAsync("B");
        await viewModel.NavigateEntryAsync("A");
        int lookupCount = catalog.LookedUpQueries.Count;

        await viewModel.NavigateLookupPathAsync(2);

        Assert.Equal(lookupCount, catalog.LookedUpQueries.Count);
        Assert.Equal(new[] { "A", "B", "A" }, viewModel.LookupPath.ToArray());
    }

    [Fact]
    public async Task EarlierDuplicatePathItem_TruncatesByIndexAndCanRestoreSnapshot()
    {
        FakeCatalog catalog = new();
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();
        await viewModel.LookupAsync("A");
        await viewModel.NavigateEntryAsync("B");
        await viewModel.NavigateEntryAsync("A");

        await viewModel.NavigateLookupPathAsync(0);

        Assert.Equal(new[] { "A" }, viewModel.LookupPath.ToArray());
        await viewModel.BackCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "A", "B", "A" }, viewModel.LookupPath.ToArray());
    }

    [Fact]
    public async Task EntryNavigation_DeduplicatesTailIgnoringCase()
    {
        FakeCatalog catalog = new();
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();
        await viewModel.LookupAsync("Word");

        await viewModel.NavigateEntryAsync("word");

        Assert.Equal(new[] { "Word" }, viewModel.LookupPath.ToArray());
    }

    [Fact]
    public async Task WordFragment_LooksUpWordAndCarriesFragmentToDocument()
    {
        FakeCatalog catalog = new();
        catalog.LookupHandler = (query, _) => Task.FromResult<ImmutableArray<LookupResult>>(
            [new LookupResult(new DictionaryId("dict"), "Dict", query.Text, ["<p id=\"usage\">body</p>"])]);
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();

        await viewModel.LookupAsync("word#usage");

        Assert.Equal("word", Assert.Single(catalog.LookedUpQueries));
        Assert.Equal("usage", viewModel.Document!.TargetFragment);
    }

    [Fact]
    public async Task DictionaryFailure_PreservesHealthyResultsAndAddsNotice()
    {
        FakeCatalog catalog = new()
        {
            LookupBatchHandler = (_, _) => Task.FromResult(new LookupBatchResult(
                [new LookupResult(new DictionaryId("healthy"), "Healthy", "word", ["body"])],
                ["Broken：lookup failed"]))
        };
        using ReaderViewModel viewModel = new(catalog, new ImmediateUiDispatcher());
        viewModel.MarkCatalogReady();

        await viewModel.LookupAsync("word");

        Assert.Single(viewModel.Document!.Frames);
        Assert.Contains("Broken：lookup failed", viewModel.Document.Notices);
    }

    [Fact]
    public async Task Zoom_PersistsAcrossLookupAndNewViewModel()
    {
        FakeCatalog catalog = new();
        using (ReaderViewModel first = new(
                   catalog,
                   new ImmediateUiDispatcher(),
                   TimeSpan.Zero,
                   TimeSpan.Zero))
        {
            first.MarkCatalogReady();
            first.SetZoom(1.4);
            await first.LookupAsync("word");
            await WaitUntilAsync(() =>
                Math.Abs(catalog.Settings.Appearance.ContentZoom - 1.4) < 0.001);
            Assert.Equal(1.4, first.Zoom, 3);
        }

        using ReaderViewModel restarted = new(catalog, new ImmediateUiDispatcher());

        Assert.Equal(1.4, restarted.Zoom, 3);
    }

    [Fact]
    public async Task ZoomPersistence_ChangesRevisionWithoutChangingCatalogGeneration()
    {
        FakeCatalog catalog = new() { Generation = 7, Revision = 11 };
        using ReaderViewModel viewModel = new(
            catalog,
            new ImmediateUiDispatcher(),
            zoomPersistenceDebounce: TimeSpan.Zero);
        viewModel.MarkCatalogReady();

        viewModel.SetZoom(1.3);
        await WaitUntilAsync(() => catalog.Revision == 12);

        Assert.Equal(7, catalog.Generation);
        Assert.Equal(1.3, catalog.Settings.Appearance.ContentZoom, 3);
    }

    [Fact]
    public async Task PendingZoomPatch_DoesNotOverwriteExplicitNewerZoomEdit()
    {
        FakeCatalog catalog = new();
        using ReaderViewModel viewModel = new(
            catalog,
            new ImmediateUiDispatcher(),
            zoomPersistenceDebounce: TimeSpan.FromMilliseconds(50));
        viewModel.MarkCatalogReady();
        viewModel.SetZoom(1.4);
        ApplicationSettings explicitSettings = catalog.GetSettingsSnapshot() with
        {
            Appearance = catalog.GetSettingsSnapshot().Appearance with
            {
                ContentZoom = 0.8
            }
        };
        SettingsPrepareResult preparation =
            await catalog.PrepareUpdateAsync(explicitSettings);
        SettingsCommitResult explicitCommit =
            await catalog.CommitUpdateAsync(preparation.Plan!);

        await Task.Delay(100);

        Assert.True(explicitCommit.Succeeded);
        Assert.Equal(0.8, catalog.Settings.Appearance.ContentZoom, 3);
        Assert.Equal(1, catalog.Revision);
    }

    [Fact]
    public async Task Diagnostics_LogStructuredLookupStagesWithoutQueryText()
    {
        FakeCatalog catalog = new();
        SettingsPrepareResult preparation = await catalog.PrepareUpdateAsync(
            catalog.GetSettingsSnapshot() with { CompatibilityDiagnosticsEnabled = true });
        _ = await catalog.CommitUpdateAsync(preparation.Plan!);
        catalog.LookupHandler = (query, _) => Task.FromResult<ImmutableArray<LookupResult>>(
            [new LookupResult(new DictionaryId("dict"), "Dict", query.Text, ["<img src=\"a.png\">"])]);
        RecordingLogger logger = new();
        using ReaderViewModel viewModel = new(
            catalog,
            new ImmediateUiDispatcher(),
            logger: logger);
        viewModel.MarkCatalogReady();

        await viewModel.LookupCandidateAsync("private-query");

        string message = Assert.Single(
            logger.Messages,
            item => item.Source == "reader-timeline").Message;
        Assert.Contains("Schema=reader-timeline-v1", message);
        Assert.Contains("Stage=LookupSummary", message);
        Assert.Contains("Origin=Candidate", message);
        Assert.Contains("SelectionFeedbackMs=", message);
        Assert.Contains("QueryCompleteMs=", message);
        Assert.Contains("EntryRewriteMs=", message);
        Assert.Contains("DocumentAssemblyMs=", message);
        Assert.DoesNotContain("private-query", message);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("测试条件未在期限内满足。");
            }

            await Task.Delay(10);
        }
    }

    private static DictionaryDefinition Dictionary(string id) => new(
        new DictionaryId(id),
        id,
        null,
        $"{id}.mdx",
        true,
        true,
        [],
        [],
        DictionaryAppearance.Default);

    private sealed class RecordingLogger : IAppLogger
    {
        public List<(string Source, string Message)> Messages { get; } = [];

        public void Write(
            AppLogLevel level,
            string source,
            string message,
            Exception? exception = null) =>
            Messages.Add((source, message));
    }

    private sealed class TrackingUiDispatcher : IUiDispatcher
    {
        private readonly AsyncLocal<int> _dispatchDepth = new();

        public bool HasThreadAccess => _dispatchDepth.Value > 0;

        public Task EnqueueAsync(
            Action action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _dispatchDepth.Value++;
            try
            {
                action();
                return Task.CompletedTask;
            }
            finally
            {
                _dispatchDepth.Value--;
            }
        }
    }
}
